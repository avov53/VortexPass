namespace VortexPass.Core.Models;

public sealed class ExternalComponentManifest
{
    public ExternalComponentSource Source { get; set; } = new();
    public List<ExternalComponentDefinition> Components { get; set; } = [];
}

public sealed class ExternalComponentSource
{
    public string Repository { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
}

public sealed class ExternalComponentDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public List<RequiredFileDefinition> RequiredFiles { get; set; } = [];
}

public sealed class RequiredFileDefinition
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
