using System.Security.Claims;
using _1Rad.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace _1Rad.Infrastructure.Authentication;

public class UserContext : IUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid UserId
    {
        get
        {
            var sub = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) 
                      ?? _httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
            return sub != null ? Guid.Parse(sub) : Guid.Empty;
        }
    }

    public Guid HospitalId
    {
        get
        {
            var cid = _httpContextAccessor.HttpContext?.User.FindFirstValue("cid");
            return cid != null ? Guid.Parse(cid) : Guid.Empty;
        }
    }

    public IEnumerable<int> RoleIds
    {
        get
        {
            var rid = _httpContextAccessor.HttpContext?.User.FindFirstValue("rid");
            if (string.IsNullOrEmpty(rid)) return Enumerable.Empty<int>();

            return rid.Split(',', StringSplitOptions.RemoveEmptyEntries)
                       .Select(int.Parse);
        }
    }

    public Guid? GroupId
    {
        get
        {
            var gid = _httpContextAccessor.HttpContext?.User.FindFirstValue("gid");
            return !string.IsNullOrEmpty(gid) ? Guid.Parse(gid) : null;
        }
    }

    public IEnumerable<Guid> AuthorizedHospitalIds
    {
        get
        {
            var hubs = _httpContextAccessor.HttpContext?.User.FindFirstValue("hubs");
            if (string.IsNullOrEmpty(hubs)) return Enumerable.Empty<Guid>();
            
            return hubs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                       .Select(Guid.Parse);
        }
    }
}
