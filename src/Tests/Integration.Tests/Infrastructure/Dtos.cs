namespace Integration.Tests.Infrastructure;

public sealed class TokenResult
{
    public string AccessToken { get; set; } = default!;
    public string RefreshToken { get; set; } = default!;
    public DateTime RefreshTokenExpiresAt { get; set; }
    public DateTime AccessTokenExpiresAt { get; set; }
}

public sealed class TokenRefreshResult
{
    public string Token { get; set; } = default!;
    public string RefreshToken { get; set; } = default!;
    public DateTime RefreshTokenExpiryTime { get; set; }
}

public sealed class RegisterResult
{
    public string UserId { get; set; } = default!;
}

public sealed class UserDto
{
    public string Id { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public bool IsActive { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? PhoneNumber { get; set; }
    public string? ImageUrl { get; set; }
    public string? Locale { get; set; }
}

public sealed class RoleDto
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
}

public sealed class GroupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool IsSystemGroup { get; set; }
    public int MemberCount { get; set; }
}

public sealed class CreateTenantResult
{
    public string Id { get; set; } = default!;
    public string? ProvisioningCorrelationId { get; set; }
    public string? Status { get; set; }
}

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public long TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNext { get; set; }
    public bool HasPrevious { get; set; }
}
