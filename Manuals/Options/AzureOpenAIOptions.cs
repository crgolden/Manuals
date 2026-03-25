using System.ComponentModel.DataAnnotations;

namespace Manuals.Options;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string DeploymentName { get; set; } = "gpt-4o";
}
