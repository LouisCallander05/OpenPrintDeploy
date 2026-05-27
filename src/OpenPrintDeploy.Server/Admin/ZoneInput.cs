using System.ComponentModel.DataAnnotations;

namespace OpenPrintDeploy.Server.Admin;

/// <summary>Edit model for a zone, bound by the create/edit form.</summary>
public sealed class ZoneInput
{
    [Required(ErrorMessage = "Zone name is required.")]
    [StringLength(128)]
    public string Name { get; set; } = "";

    [Range(0, 1000, ErrorMessage = "Priority must be between 0 and 1000.")]
    public int Priority { get; set; }

    /// <summary>Must be one of <see cref="PrinterIds"/>, or null for "no default".</summary>
    public Guid? DefaultPrinterId { get; set; }

    /// <summary>Ids of the printers assigned to this zone.</summary>
    public List<Guid> PrinterIds { get; set; } = [];

    public List<RuleInput> Rules { get; set; } = [];
}

/// <summary>
/// One matching rule. A null/blank criterion is ignored by the evaluator;
/// all non-null criteria within a rule must match. A rule with no criteria
/// set never matches, so the form rejects it.
/// </summary>
public sealed class RuleInput
{
    [StringLength(256)]
    public string? GroupSid { get; set; }

    [StringLength(64)]
    public string? SubnetCidr { get; set; }

    [StringLength(512)]
    public string? OuDn { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(GroupSid)
        && string.IsNullOrWhiteSpace(SubnetCidr)
        && string.IsNullOrWhiteSpace(OuDn);
}
