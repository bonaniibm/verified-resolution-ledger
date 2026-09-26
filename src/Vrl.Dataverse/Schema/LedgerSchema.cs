namespace Vrl.Dataverse.Schema;

/// <summary>Single source of truth for logical names. Web resources and Power BI use the same names – see docs/Data-Model.md.</summary>
public static class LedgerSchema
{
    public const string Prefix = "bpc";
    public const string SolutionUniqueName = "VerifiedResolutionLedger";
    public const string SolutionDisplayName = "Verified Resolution Ledger";
    public const string PublisherUniqueName = "contactcenteraccelerators";
    public const string PublisherDisplayName = "Contact Center Accelerators";
    public const int OptionValuePrefix = 10000;

    public const string GlobalChannelChoice = "bpc_channel";

    public static class Interaction
    {
        public const string Table = "bpc_interaction";
        public const string Id = "bpc_interactionid";
        public const string Name = "bpc_name";
        public const string SourceSystem = "bpc_sourcesystem";
        public const string SourceRecordId = "bpc_sourcerecordid";
        public const string SourceKey = "bpc_sourcekey"; // alternate key
        public const string Channel = "bpc_channel";
        public const string HandlingMode = "bpc_handlingmode";
        public const string NativeBotOutcome = "bpc_nativebotoutcome";
        public const string StartedOn = "bpc_startedon";
        public const string EndedOn = "bpc_endedon";
        public const string ContactId = "bpc_contactid";
        public const string AccountId = "bpc_accountid";
        public const string CustomerKey = "bpc_customerkey";
        public const string IdentityConfidence = "bpc_identityconfidence";
        public const string IntentCode = "bpc_intentcode";
        public const string IntentFamily = "bpc_intentfamily";
        public const string DispositionCode = "bpc_dispositioncode";
        public const string CaseId = "bpc_caseid";
        public const string Summary = "bpc_summary";
        public const string QueueName = "bpc_queuename";
        public const string BotTopic = "bpc_bottopic";
        public const string KnowledgeArticleId = "bpc_knowledgearticleid";
        public const string FollowUpScheduled = "bpc_followupscheduled";
        public const string BotMinutes = "bpc_botminutes";
        public const string HandleMinutes = "bpc_handleminutes";
        public const string TelephonyMinutes = "bpc_telephonyminutes";
        public const string AiCredits = "bpc_aicredits";
        public const string Embedding = "bpc_embedding";
        public const string EmbeddingModel = "bpc_embeddingmodel";
        // Evaluation output
        public const string EpisodeId = "bpc_episodeid";
        public const string EpisodeKey = "bpc_episodekey";
        public const string PredecessorId = "bpc_predecessorid";
        public const string SuccessorId = "bpc_successorid";
        public const string PredecessorScore = "bpc_predecessorscore";
        public const string Verdict = "bpc_verdict";
        public const string VerdictFinal = "bpc_verdictfinal";
        public const string VerdictCode = "bpc_verdictcode";
        public const string VerdictReason = "bpc_verdictreason";
        public const string WindowClosesOn = "bpc_windowcloseson";
        public const string EvaluatedOn = "bpc_evaluatedon";
        public const string EngineVersion = "bpc_engineversion";
        public const string CostAi = "bpc_costai";
        public const string CostTelephony = "bpc_costtelephony";
        public const string CostLabor = "bpc_costlabor";
        public const string CostTotal = "bpc_costtotal";
    }

    public static class Episode
    {
        public const string Table = "bpc_contactepisode";
        public const string Id = "bpc_contactepisodeid";
        public const string Name = "bpc_name";
        public const string EpisodeKey = "bpc_episodekey";
        public const string EpisodeKeyAltKey = "bpc_episodekey_key";
        public const string CustomerKey = "bpc_customerkey";
        public const string ContactId = "bpc_contactid";
        public const string Status = "bpc_status";
        public const string ContactCount = "bpc_contactcount";
        public const string FirstStartedOn = "bpc_firststartedon";
        public const string LastEndedOn = "bpc_lastendedon";
        public const string TotalCost = "bpc_totalcost";
        public const string RootCauseIntent = "bpc_rootcauseintent";
        public const string RootCauseTopic = "bpc_rootcausetopic";
        public const string RootCauseQueue = "bpc_rootcausequeue";
        public const string Channels = "bpc_channels";
        public const string EngineVersion = "bpc_engineversion";
    }

    public static class WindowPolicy
    {
        public const string Table = "bpc_windowpolicy";
        public const string Name = "bpc_name";
        public const string Scope = "bpc_scope";
        public const string Channel = "bpc_channel";
        public const string IntentFamily = "bpc_intentfamily";
        public const string WindowHours = "bpc_windowhours";
        public const string SameIssueThreshold = "bpc_sameissuethreshold";
    }

    public static class CostRate
    {
        public const string Table = "bpc_costrate";
        public const string Name = "bpc_name";
        public const string RateType = "bpc_ratetype";
        public const string Channel = "bpc_channel";
        public const string QueueName = "bpc_queuename";
        public const string Amount = "bpc_amount";
        public const string Currency = "bpc_currencycode";
    }

    public enum PolicyScope { Global = 100000000, Channel = 100000001, IntentFamily = 100000002 }

    public enum RateType { CreditPrice = 100000000, TelephonyPerMinute = 100000001, LaborPerHour = 100000002, ChannelPerMinute = 100000003 }
}
