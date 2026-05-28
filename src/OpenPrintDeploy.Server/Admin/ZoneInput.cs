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

    /// <summary>Ids of the printers assigned to this zone.</summary>
    public List<Guid> PrinterIds { get; set; } = [];

    public List<RuleInput> Rules { get; set; } = [];
}

/// <summary>
/// One matching rule: the user must be a member of the group with this SID.
/// A rule with no group is invalid and rejected by the form.
/// </summary>
public sealed class RuleInput
{
    /// <summary>
    /// The group as typed/picked in the admin UI — a friendly name or a raw SID.
    /// The Zones page resolves this to <see cref="GroupSid"/> via the directory
    /// before saving; it is never persisted itself.
    /// </summary>
    [StringLength(256)]
    public string? GroupName { get; set; }

    [StringLength(256)]
    public string? GroupSid { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(GroupSid);
}
