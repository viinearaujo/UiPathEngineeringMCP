namespace UiPath.Engineering.Mcp.Core;

// Stable error codes for the structured ToolError taxonomy. Clients may branch
// on these values, so treat them as part of the public contract.
public static class ToolErrorCodes {
    public const string SpecUnknownActivity = "SPEC_UNKNOWN_ACTIVITY";
    public const string SpecMissingRequiredProperty = "SPEC_MISSING_REQUIRED_PROPERTY";
    public const string SpecInvalidNesting = "SPEC_INVALID_NESTING";
    public const string SpecValueFormMismatch = "SPEC_VALUE_FORM_MISMATCH";
    public const string SpecEmptySpec = "SPEC_EMPTY_SPEC";
    public const string SpecInvalidSpecJson = "SPEC_INVALID_SPEC_JSON";
    public const string XamlRenderFailed = "XAML_RENDER_FAILED";
    public const string XamlRoundtripFailed = "XAML_ROUNDTRIP_FAILED";
    public const string XamlParseFailed = "XAML_PARSE_FAILED";
    public const string DataDeclarationConflict = "DATA_DECLARATION_CONFLICT";
    public const string DataDeclarationNotFound = "DATA_DECLARATION_NOT_FOUND";
    public const string PathNotAllowed = "PATH_NOT_ALLOWED";
    public const string SkillsRootMissing = "SKILLS_ROOT_MISSING";
    public const string SkillNotFound = "SKILL_NOT_FOUND";
    public const string SkillPathRejected = "SKILL_PATH_REJECTED";
    public const string SkillFileNotFound = "SKILL_FILE_NOT_FOUND";
    public const string CliVerbNotAllowed = "CLI_VERB_NOT_ALLOWED";
    public const string CliArgumentsRejected = "CLI_ARGUMENTS_REJECTED";
    public const string MutatingCommandDisabled = "MUTATING_COMMAND_DISABLED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string ActivityNotFound = "ACTIVITY_NOT_FOUND";
    public const string ActivityIdStale = "ACTIVITY_ID_STALE";
    public const string AmbiguousActivity = "AMBIGUOUS_ACTIVITY";
    public const string DocsStale = "DOCS_STALE";
    public const string DocsInconsistent = "DOCS_INCONSISTENT";
    public const string DocsAdrIncomplete = "DOCS_ADR_INCOMPLETE";
    public const string XamlCodedBoundary = "XAML_CODED_BOUNDARY";
}
