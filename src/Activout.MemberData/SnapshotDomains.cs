namespace Activout.MemberData;

/// <summary>
/// Snapshot domains listed in LinkedIn's "Member Snapshot Domains" page (li-dma-data-portability-2026-08, updated
/// 2026-08-26). The list is informational: a snapshot query without a domain returns every domain that has data,
/// including undocumented ones, so new domains are picked up without a code change. Identifiers are case sensitive.
/// </summary>
public static class SnapshotDomains
{
    public const string DocumentationVersion = "2026-08";

    public static readonly IReadOnlyList<string> Documented =
    [
        "ADS_CLICKED", "MEMBER_FOLLOWING", "LOGIN", "RICH_MEDIA", "SEARCHES", "INFERENCE_TAKEOUT", "ALL_COMMENTS",
        "CONTACTS", "EVENTS", "RECEIPTS", "AD_TARGETING", "REGISTRATION", "REVIEWS", "ARTICLES", "PATENTS", "GROUPS",
        "COMPANY_FOLLOWS", "INVITATIONS", "PHONE_NUMBERS", "CONNECTIONS", "EMAIL_ADDRESSES", "JOB_POSTINGS",
        "JOB_APPLICATIONS", "JOB_SEEKER_PREFERENCES", "LEARNING", "INBOX", "SAVED_JOBS", "SAVED_JOB_ALERTS", "PROFILE",
        "SKILLS", "POSITIONS", "EDUCATION", "TEST_SCORES", "CAUSES_YOU_CARE_ABOUT", "PUBLICATIONS", "PROJECTS",
        "ORGANIZATIONS", "LANGUAGES", "HONORS", "COURSES", "CERTIFICATIONS", "RECOMMENDATIONS", "ENDORSEMENTS",
        "MEMBER_SHARE_INFO", "SECURITY_CHALLENGE_PIPE", "TRUSTED_GRAPH", "MARKETPLACE_ENGAGEMENTS",
        "MARKETPLACE_PROVIDERS", "MARKETPLACE_OPPORTUNITIES", "ACTOR_SAVE_ITEM", "JOB_APPLICANT_SAVED_ANSWERS",
        "TALENT_QUESTION_SAVED_RESPONSE", "PROFILE_SUMMARY", "ALL_LIKES", "ALL_VOTES", "RECEIPTS_LBP",
        "EASYAPPLY_BLOCKING", "LEARNING_COACH_AI_TAKEOUT", "LEARNING_COACH_INBOX", "LEARNING_ROLEPLAY_INBOX",
        "VOLUNTEERING_EXPERIENCES", "ACCOUNT_HISTORY", "INSTANT_REPOSTS", "IDENTITY_CREDENTIALS_AND_ASSETS", "ADS_LAN",
        "PREMIUM_NOTES",
    ];
}
