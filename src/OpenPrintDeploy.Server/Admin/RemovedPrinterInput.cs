using System.ComponentModel.DataAnnotations;

namespace OpenPrintDeploy.Server.Admin;

public sealed class RemovedPrinterInput
{
    [Required(ErrorMessage = "UNC path is required.")]
    [RegularExpression(
        @"^\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+$",
        ErrorMessage = @"Use a UNC path like \\server\share.")]
    [StringLength(260)]
    public string UncPath { get; set; } = @"\\";
}
