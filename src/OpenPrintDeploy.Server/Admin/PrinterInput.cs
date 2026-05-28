using System.ComponentModel.DataAnnotations;

namespace OpenPrintDeploy.Server.Admin;

/// <summary>Edit model for a printer, bound by the create/edit form.</summary>
public sealed class PrinterInput
{
    [Required(ErrorMessage = "UNC path is required.")]
    [RegularExpression(
        @"^\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+$",
        ErrorMessage = @"Use a UNC path like \\server\share.")]
    [StringLength(260)]
    public string UncPath { get; set; } = @"\\";

    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(128)]
    public string DisplayName { get; set; } = "";
}
