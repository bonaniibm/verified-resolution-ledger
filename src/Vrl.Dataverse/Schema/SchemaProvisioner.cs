using System.ServiceModel;
using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using S = Vrl.Dataverse.Schema.LedgerSchema;

namespace Vrl.Dataverse.Schema;

/// <summary>
/// Idempotently creates the publisher, unmanaged solution, global choices, tables, columns, lookups and alternate keys
/// defined in <see cref="SchemaDefinition"/>. Safe to re-run: existing components are detected and skipped (or added
/// to the solution). Export the resulting solution as managed for downstream environments (see docs/ALM.md).
/// </summary>
public sealed partial class SchemaProvisioner(IOrganizationService service, ILogger logger)
{
    private const int LanguageCode = 1033;

    /// <summary>Privileges the provisioning identity needs; checked up front so a partial run is avoided.</summary>
    private static readonly string[] RequiredPrivileges =
    [
        "prvCreateEntity", "prvCreateAttribute", "prvCreateOptionSet", "prvCreateRelationship",
        "prvCreateEntityKey", "prvCreateRole", "prvWriteRole", "prvPublishCustomizations",
    ];

    public async Task ProvisionAsync(CancellationToken ct = default)
    {
        CheckPrivileges();
        EnsurePublisherAndSolution();

        foreach (var (name, display, enumType) in SchemaDefinition.GlobalChoices)
            EnsureGlobalChoice(name, display, enumType);

        // Phase 1: tables (primary name only) so that lookups between them can be created afterwards.
        foreach (var t in SchemaDefinition.Tables) EnsureTable(t);

        // Phase 2: plain columns. Phase 3: lookups. Phase 4: alternate keys.
        foreach (var t in SchemaDefinition.Tables)
        {
            var existing = ExistingColumns(t.LogicalName);
            foreach (var c in t.Columns.Where(c => c.Kind != ColumnKind.Lookup))
                if (!existing.Contains(c.LogicalName)) CreateColumn(t.LogicalName, c);
        }

        foreach (var t in SchemaDefinition.Tables)
        {
            var existing = ExistingColumns(t.LogicalName);
            foreach (var c in t.Columns.Where(c => c.Kind == ColumnKind.Lookup))
            {
                if (existing.Contains(c.LogicalName)) continue;
                // Optional targets (e.g. 'incident' only exists when Customer Service is installed) are skipped, not failed.
                if (TryRetrieveEntity(c.LookupTarget!, EntityFilters.Entity) is null)
                {
                    logger.LogWarning("Skipping lookup {Table}.{Column}: target table '{Target}' is not installed in this environment",
                        t.LogicalName, c.LogicalName, c.LookupTarget);
                    continue;
                }
                if (c.LookupTarget!.StartsWith(S.Prefix + "_", StringComparison.OrdinalIgnoreCase))
                {
                    CreateLookup(t.LogicalName, c); // ledger-internal lookups are required
                    continue;
                }
                try
                {
                    CreateLookup(t.LogicalName, c);
                }
                catch (Exception ex) when (ex is FaultException<OrganizationServiceFault> or TimeoutException)
                {
                    // Lookups to CRM tables (contact, account, incident) are enrichments; don't fail the whole deployment.
                    logger.LogWarning("Optional lookup {Table}.{Column} -> {Target} not created ({Message}); re-run provisioning later to add it",
                        t.LogicalName, c.LogicalName, c.LookupTarget, ex.Message.Split('.')[0]);
                }
            }
        }

        foreach (var t in SchemaDefinition.Tables)
            foreach (var key in t.AlternateKeys)
                EnsureAlternateKey(t.LogicalName, key.SchemaName, key.DisplayName, key.Columns);

        logger.LogInformation("Publishing customizations…");
        service.Execute(new PublishAllXmlRequest());

        foreach (var t in SchemaDefinition.Tables.Where(t => t.AlternateKeys.Count > 0))
            await WaitForKeysAsync(t.LogicalName, ct);

        EnsureRoles();

        logger.LogInformation("Schema provisioning complete.");
    }

    private void CheckPrivileges()
    {
        var who = (WhoAmIResponse)service.Execute(new WhoAmIRequest());
        var missing = new List<string>();
        foreach (var name in RequiredPrivileges)
        {
            try
            {
                var resp = (RetrieveUserPrivilegeByPrivilegeNameResponse)service.Execute(
                    new RetrieveUserPrivilegeByPrivilegeNameRequest { PrivilegeName = name, UserId = who.UserId });
                if (resp.RolePrivileges.Length == 0) missing.Add(name);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                // Unknown privilege name in this version: don't block on the pre-check, the real call will tell.
                logger.LogDebug("Privilege {Name} could not be checked: {Message}", name, ex.Message);
            }
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The signed-in user ({who.UserId}) lacks {string.Join(", ", missing)}. Assign the 'System Administrator' " +
                "role in this environment (Power Platform admin center → Environments → <env> → Settings → Users → Manage security roles), then re-run.");
        logger.LogInformation("Privilege check passed for {User}", who.UserId);
    }

    private void EnsurePublisherAndSolution()
    {
        var publisher = service.RetrieveMultiple(new QueryExpression("publisher")
        {
            ColumnSet = new ColumnSet("publisherid", "customizationprefix"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, S.PublisherUniqueName) } },
        }).Entities.FirstOrDefault();

        Guid publisherId;
        if (publisher is null)
        {
            publisherId = service.Create(new Entity("publisher")
            {
                ["uniquename"] = S.PublisherUniqueName,
                ["friendlyname"] = S.PublisherDisplayName,
                ["customizationprefix"] = S.Prefix,
                ["customizationoptionvalueprefix"] = S.OptionValuePrefix,
                ["description"] = "Publisher for Contact Center accelerators.",
            });
            logger.LogInformation("Created publisher {Publisher}", S.PublisherUniqueName);
        }
        else
        {
            publisherId = publisher.Id;
            var prefix = publisher.GetAttributeValue<string>("customizationprefix");
            if (!string.Equals(prefix, S.Prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Publisher '{S.PublisherUniqueName}' exists with prefix '{prefix}', expected '{S.Prefix}'.");
        }

        var solution = service.RetrieveMultiple(new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, S.SolutionUniqueName) } },
        }).Entities.FirstOrDefault();

        if (solution is null)
        {
            service.Create(new Entity("solution")
            {
                ["uniquename"] = S.SolutionUniqueName,
                ["friendlyname"] = S.SolutionDisplayName,
                ["publisherid"] = new EntityReference("publisher", publisherId),
                ["version"] = "1.0.0.0",
                ["description"] = "Verified Resolution Ledger – true containment, interaction-derived FCR and cost per resolved outcome.",
            });
            logger.LogInformation("Created solution {Solution}", S.SolutionUniqueName);
        }
    }

    private void EnsureGlobalChoice(string name, string displayName, Type enumType)
    {
        try
        {
            service.Execute(new RetrieveOptionSetRequest { Name = name });
            logger.LogInformation("Global choice {Name} exists", name);
            return;
        }
        catch (FaultException<OrganizationServiceFault>) { /* not found */ }

        var os = new OptionSetMetadata
        {
            Name = name,
            DisplayName = L(displayName),
            IsGlobal = true,
            OptionSetType = OptionSetType.Picklist,
        };
        foreach (var o in Options(enumType)) os.Options.Add(o);
        service.Execute(new CreateOptionSetRequest { OptionSet = os, SolutionUniqueName = S.SolutionUniqueName });
        logger.LogInformation("Created global choice {Name}", name);
    }

    private void EnsureTable(TableDef t)
    {
        var existing = TryRetrieveEntity(t.LogicalName, EntityFilters.Entity);
        if (existing is not null)
        {
            service.Execute(new AddSolutionComponentRequest
            {
                ComponentType = 1, // Entity
                ComponentId = existing.MetadataId!.Value,
                SolutionUniqueName = S.SolutionUniqueName,
                AddRequiredComponents = false,
                DoNotIncludeSubcomponents = false,
            });
            logger.LogInformation("Table {Table} exists (ensured in solution)", t.LogicalName);
            return;
        }

        service.Execute(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = t.LogicalName,
                DisplayName = L(t.DisplayName),
                DisplayCollectionName = L(t.PluralName),
                Description = L(t.Description),
                OwnershipType = OwnershipTypes.UserOwned,
                IsActivity = false,
                IsAuditEnabled = new BooleanManagedProperty(true),
                ChangeTrackingEnabled = true, // required for Fabric Link / Synapse Link
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = t.PrimaryNameColumn,
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                MaxLength = 200,
                FormatName = StringFormatName.Text,
                DisplayName = L("Name"),
                Description = L("Primary name."),
            },
            HasActivities = false,
            HasNotes = false,
            SolutionUniqueName = S.SolutionUniqueName,
        });
        logger.LogInformation("Created table {Table}", t.LogicalName);
    }

    private void CreateColumn(string table, ColumnDef c)
    {
        AttributeMetadata attr = c.Kind switch
        {
            ColumnKind.Text => new StringAttributeMetadata { MaxLength = c.MaxLength, FormatName = StringFormatName.Text },
            ColumnKind.Memo => new MemoAttributeMetadata { MaxLength = c.MaxLength, Format = StringFormat.TextArea },
            ColumnKind.Integer => new IntegerAttributeMetadata { MinValue = 0, MaxValue = int.MaxValue, Format = IntegerFormat.None },
            ColumnKind.Decimal => new DecimalAttributeMetadata { Precision = c.Precision, MinValue = -100_000_000_000m, MaxValue = 100_000_000_000m },
            ColumnKind.Boolean => new BooleanAttributeMetadata
            {
                OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(L("Yes"), 1), new OptionMetadata(L("No"), 0)),
                DefaultValue = false,
            },
            ColumnKind.DateTime => new DateTimeAttributeMetadata
            {
                Format = DateTimeFormat.DateAndTime,
                DateTimeBehavior = DateTimeBehavior.UserLocal,
            },
            ColumnKind.LocalChoice => new PicklistAttributeMetadata { OptionSet = LocalOptionSet(c.ChoiceEnum!) },
            ColumnKind.GlobalChoice => new PicklistAttributeMetadata
            {
                OptionSet = new OptionSetMetadata { IsGlobal = true, Name = c.GlobalChoiceName },
            },
            _ => throw new NotSupportedException(c.Kind.ToString()),
        };

        attr.SchemaName = c.LogicalName;
        attr.LogicalName = c.LogicalName;
        attr.DisplayName = L(c.DisplayName);
        attr.Description = L(c.Description);
        attr.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        attr.IsAuditEnabled = new BooleanManagedProperty(c.LogicalName is S.Interaction.Verdict or S.Interaction.VerdictFinal);

        service.Execute(new CreateAttributeRequest { EntityName = table, Attribute = attr, SolutionUniqueName = S.SolutionUniqueName });
        logger.LogInformation("  + {Table}.{Column} ({Kind})", table, c.LogicalName, c.Kind);
    }

    private void CreateLookup(string table, ColumnDef c)
    {
        service.Execute(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = c.RelationshipName,
                ReferencedEntity = c.LookupTarget,
                ReferencingEntity = table,
                AssociatedMenuConfiguration = new AssociatedMenuConfiguration
                {
                    Behavior = AssociatedMenuBehavior.UseCollectionName,
                    Group = AssociatedMenuGroup.Details,
                    Order = 10000,
                },
                // RemoveLink everywhere: deleting a contact (e.g. GDPR erasure) must not delete aggregate ledger rows,
                // and ledger rows must never cascade-delete CRM data.
                CascadeConfiguration = new CascadeConfiguration
                {
                    Assign = CascadeType.NoCascade,
                    Delete = CascadeType.RemoveLink,
                    Merge = CascadeType.NoCascade,
                    Reparent = CascadeType.NoCascade,
                    Share = CascadeType.NoCascade,
                    Unshare = CascadeType.NoCascade,
                    RollupView = CascadeType.NoCascade,
                },
            },
            Lookup = new LookupAttributeMetadata
            {
                SchemaName = c.LogicalName,
                DisplayName = L(c.DisplayName),
                Description = L(c.Description),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
            SolutionUniqueName = S.SolutionUniqueName,
        });
        logger.LogInformation("  + {Table}.{Column} → {Target}", table, c.LogicalName, c.LookupTarget);
    }

    private void EnsureAlternateKey(string table, string schemaName, string displayName, string[] columns)
    {
        var meta = TryRetrieveEntity(table, EntityFilters.Entity);
        if (meta?.Keys?.Any(k => string.Equals(k.LogicalName, schemaName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            logger.LogInformation("Alternate key {Key} exists", schemaName);
            return;
        }

        try
        {
            service.Execute(new CreateEntityKeyRequest
            {
                EntityName = table,
                EntityKey = new EntityKeyMetadata { SchemaName = schemaName, DisplayName = L(displayName), KeyAttributes = columns },
                SolutionUniqueName = S.SolutionUniqueName,
            });
            logger.LogInformation("Created alternate key {Key} on {Table}", schemaName, table);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Keys are not always returned by the lightweight metadata call (and a full retrieve is expensive in large
            // orgs), e.g. after a solution import. An existing key is the desired end state.
            logger.LogInformation("Alternate key {Key} exists on {Table}", schemaName, table);
        }
    }

    private async Task WaitForKeysAsync(string table, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var keys = TryRetrieveEntity(table, EntityFilters.Entity)?.Keys ?? [];
            var failed = keys.FirstOrDefault(k => k.EntityKeyIndexStatus == EntityKeyIndexStatus.Failed);
            if (failed is not null)
                throw new InvalidOperationException($"Alternate key {failed.LogicalName} index failed; check for duplicate data and reactivate the key.");
            if (keys.All(k => k.EntityKeyIndexStatus == EntityKeyIndexStatus.Active))
            {
                logger.LogInformation("Alternate keys on {Table} are active", table);
                return;
            }
            logger.LogInformation("Waiting for alternate key index on {Table}…", table);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        logger.LogWarning("Alternate keys on {Table} are still building; upserts by key will fail until they are active", table);
    }

    /// <summary>
    /// Least-privilege roles. Assign "VRL Ledger Service" (+ Basic User) to the managed identity's application user and
    /// "VRL Ledger Reader" to supervisors/representatives who use the side pane or dashboard.
    /// </summary>
    public static readonly (string Name, string[] Ledger, string[] Access, string[] ReadOnly, string[] AppendTo)[] Roles =
    [
        ("VRL Ledger Service",
            [S.Interaction.Table, S.Episode.Table],
            ["Create", "Read", "Write", "Delete", "Append", "AppendTo"],
            [S.WindowPolicy.Table, S.CostRate.Table, "contact", "account", "incident", "msdyn_ocliveworkitem",
             "msdyn_ocsession", "msdyn_sessionparticipant", "User" /* systemuser privilege is prvReadUser */, "queue",
             "Activity" /* msdyn_ocliveworkitem and msdyn_ocsession are activity tables: covered by prvReadActivity */,
             "msdyn_transcript", "Note" /* transcript text is stored as an annotation (prvReadNote) */,
             "msdyn_ocliveworkitemcontextitem" /* pre-chat answers, used for self-declared identity */,
             "conversationtranscript" /* Copilot Studio transcripts: which topic the AI agent handled */],
            // Setting a lookup requires AppendTo on the *target* table.
            ["contact", "account", "incident"]),
        ("VRL Ledger Reader",
            [S.Interaction.Table, S.Episode.Table, S.WindowPolicy.Table, S.CostRate.Table],
            ["Read"],
            [],
            []),
    ];

    private void EnsureRoles()
    {
        var rootBu = service.RetrieveMultiple(new QueryExpression("businessunit")
        {
            ColumnSet = new ColumnSet("businessunitid"),
            Criteria = { Conditions = { new ConditionExpression("parentbusinessunitid", ConditionOperator.Null) } },
        }).Entities.First().Id;

        foreach (var (name, ledger, access, readOnly, appendTo) in Roles)
        {
            var existing = service.RetrieveMultiple(new QueryExpression("role")
            {
                ColumnSet = new ColumnSet("roleid"),
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("name", ConditionOperator.Equal, name),
                        new ConditionExpression("businessunitid", ConditionOperator.Equal, rootBu),
                    },
                },
            }).Entities.FirstOrDefault();

            var roleId = existing?.Id ?? service.Create(new Entity("role")
            {
                ["name"] = name,
                ["businessunitid"] = new EntityReference("businessunit", rootBu),
                ["description"] = "Created by Verified Resolution Ledger provisioning.",
            });

            var wanted = ledger.SelectMany(t => access.Select(a => $"prv{a}{t}"))
                .Concat(readOnly.Select(t => $"prvRead{t}"))
                .Concat(appendTo.Select(t => $"prvAppendTo{t}"))
                .ToList();

            var privileges = service.RetrieveMultiple(new QueryExpression("privilege")
            {
                ColumnSet = new ColumnSet("privilegeid", "name"),
                Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.In, wanted.Cast<object>().ToArray()) } },
            }).Entities;

            var missing = wanted.Except(privileges.Select(p => p.GetAttributeValue<string>("name")), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                logger.LogInformation("Role {Role}: skipping privileges for tables not in this environment: {Missing}", name, string.Join(", ", missing));

            service.Execute(new AddPrivilegesRoleRequest
            {
                RoleId = roleId,
                Privileges = privileges.Select(p => new RolePrivilege((int)PrivilegeDepth.Global, p.Id)).ToArray(),
            });

            service.Execute(new AddSolutionComponentRequest
            {
                ComponentType = 20, // Role
                ComponentId = roleId,
                SolutionUniqueName = S.SolutionUniqueName,
                AddRequiredComponents = false,
            });
            logger.LogInformation("Ensured security role {Role} ({Count} privileges)", name, privileges.Count);
        }
    }

    private HashSet<string> ExistingColumns(string table) =>
        (TryRetrieveEntity(table, EntityFilters.Attributes)?.Attributes ?? [])
            .Select(a => a.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private EntityMetadata? TryRetrieveEntity(string logicalName, EntityFilters filters)
    {
        try
        {
            var resp = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = filters,
                RetrieveAsIfPublished = true,
            });
            return resp.EntityMetadata;
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            return null;
        }
    }

    private static OptionSetMetadata LocalOptionSet(Type enumType)
    {
        var os = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        foreach (var o in Options(enumType)) os.Options.Add(o);
        return os;
    }

    private static IEnumerable<OptionMetadata> Options(Type enumType) =>
        Enum.GetValues(enumType).Cast<object>()
            .Select(v => new OptionMetadata(L(Humanize(Enum.GetName(enumType, v)!)), Convert.ToInt32(v)));

    internal static string Humanize(string pascal) => PascalSplit().Replace(pascal, " $1").Trim();

    private static Label L(string text) => new(text, LanguageCode);

    [GeneratedRegex(@"(?<=[a-z])([A-Z])")]
    private static partial Regex PascalSplit();
}
