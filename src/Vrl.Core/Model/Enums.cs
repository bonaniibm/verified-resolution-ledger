namespace Vrl.Core.Model;

// Numeric values are the Dataverse choice values used by the bpc_* tables.
// Keep them stable: they are persisted and referenced by reports.

/// <summary>Normalised channel, independent of the source system's own option set.</summary>
public enum Channel
{
    Unknown = 100000000,
    Chat = 100000001,
    Voice = 100000002,
    Sms = 100000003,
    Email = 100000004,
    Social = 100000005,
    Case = 100000006,
}

/// <summary>Who actually handled the interaction.</summary>
public enum HandlingMode
{
    Unknown = 100000000,
    /// <summary>AI agent / bot only – the population "containment" is claimed on.</summary>
    BotOnly = 100000001,
    /// <summary>Bot started, escalated to a human in the same conversation.</summary>
    BotThenHuman = 100000002,
    HumanOnly = 100000003,
    /// <summary>Back-office case work with no live conversation.</summary>
    CaseOnly = 100000004,
}

/// <summary>What the source platform claims happened (Copilot Studio session outcome / Omnichannel flags).</summary>
public enum NativeBotOutcome
{
    None = 100000000,
    Resolved = 100000001,
    /// <summary>Copilot Studio "implied resolved": session ended without user confirmation.</summary>
    ResolvedImplied = 100000002,
    Escalated = 100000003,
    Abandoned = 100000004,
}

/// <summary>The ledger's verified outcome for a single interaction.</summary>
public enum OutcomeVerdict
{
    /// <summary>Repeat window still open; nothing can be concluded yet.</summary>
    Pending = 100000000,
    /// <summary>Window elapsed with no same-issue return contact.</summary>
    VerifiedResolved = 100000001,
    /// <summary>Bot-only interaction followed by a same-issue repeat contact.</summary>
    FalseContainment = 100000002,
    /// <summary>Human-handled interaction followed by a same-issue repeat contact.</summary>
    FailedHumanResolution = 100000003,
    /// <summary>A repeat followed, but a follow-up had been deliberately scheduled.</summary>
    PlannedFollowUp = 100000004,
    /// <summary>Cannot be verified (no customer identity, or silent abandonment).</summary>
    Unknown = 100000005,
}

public enum IdentityConfidence
{
    None = 100000000,
    Low = 100000001,
    Medium = 100000002,
    High = 100000003,
}

public enum EpisodeStatus
{
    Open = 100000000,
    Resolved = 100000001,
    Unresolved = 100000002,
    Unknown = 100000003,
}
