using System;
using System.Collections.Generic;

namespace ImagingAI.Api.Models.Database;

public partial class Role
{
    public long RoleId { get; set; }

    public string RoleCode { get; set; } = null!;

    public string RoleName { get; set; } = null!;

    public virtual ICollection<UserAccount> UserAccounts { get; set; } = new List<UserAccount>();

}
