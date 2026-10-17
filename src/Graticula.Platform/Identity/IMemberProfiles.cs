using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// A member's profile, their photo, and the categories an organization sorts its members into.
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-10-17, by owner decision, after Portal's Members page and profile page</b> — *"categoryleme, category'e
/// göre filtreleme"*, then *"kullanıcıların profil sayfaları olsa. her kullanıcı ya da admin onlara fotoğraf
/// koyabilse."* Portal keeps a bio, a photo and a profile visibility on each member, and a category schema an
/// administrator draws and assigns members into.
/// </para>
/// <para>
/// <b>Its own port, beside <see cref="IMemberDirectory"/> rather than in it</b>, because a member edits their own
/// profile and only an administrator reaches the directory: the two are different callers with different rights,
/// and one interface for both would put member creation one method away from a self-service page.
/// </para>
/// </remarks>
public interface IMemberProfiles
{
    /// <summary>A member's profile, or null when there is no such member.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The profile.</returns>
    Task<MemberProfile?> GetProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Replaces what a member says about themselves.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="edit">The new values.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a member.</returns>
    Task<bool> SetProfileAsync(string name, ProfileEdit edit, CancellationToken cancellationToken);

    /// <summary>A member's photo, or null when they have none.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The photo.</returns>
    Task<MemberPhoto?> GetPhotoAsync(string name, CancellationToken cancellationToken);

    /// <summary>Replaces a member's photo.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="contentType">Its media type.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a member.</returns>
    Task<bool> SetPhotoAsync(string name, string contentType, byte[] content, CancellationToken cancellationToken);

    /// <summary>Removes a member's photo.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether they had one.</returns>
    Task<bool> DeletePhotoAsync(string name, CancellationToken cancellationToken);

    /// <summary>Every category, with how many members each holds directly.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The categories, parents before their children, each level by position then name.</returns>
    Task<IReadOnlyList<MemberCategory>> ListCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>Makes a category, or returns null when its parent already has one of that name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="parent">The category it sits under, or null for the top.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The category.</returns>
    Task<MemberCategory?> CreateCategoryAsync(string name, Guid? parent, CancellationToken cancellationToken);

    /// <summary>Renames a category.</summary>
    /// <param name="id">The category.</param>
    /// <param name="name">Its new name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when renamed, false when there is no such category, null when the name is taken beside it.</returns>
    Task<bool?> RenameCategoryAsync(Guid id, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a category and those under it; members in them simply stop being in them.</summary>
    /// <param name="id">The category.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a category.</returns>
    Task<bool> DeleteCategoryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Puts members into categories and takes them out of others, in one write.</summary>
    /// <param name="members">The members, by sign-in name.</param>
    /// <param name="add">Categories to put them in.</param>
    /// <param name="remove">Categories to take them out of.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many of the named members exist.</returns>
    Task<int> CategorizeAsync(
        IReadOnlyList<string> members, IReadOnlyList<Guid> add, IReadOnlyList<Guid> remove,
        CancellationToken cancellationToken);
}

/// <summary>What a member's profile says.</summary>
/// <param name="Id">Their principal.</param>
/// <param name="Name">Their sign-in name.</param>
/// <param name="DisplayName">What they are shown as, or null for the name.</param>
/// <param name="Email">Their e-mail address, or null.</param>
/// <param name="Bio">What they say about themselves, or null.</param>
/// <param name="Visibility"><c>private</c>, <c>organization</c> or <c>public</c>: who may read this profile.</param>
/// <param name="CreatedAt">When the account was made.</param>
/// <param name="IsDisabled">Whether they can sign in.</param>
/// <param name="PhotoUpdatedAt">When their photo was last set, or null when they have none.</param>
/// <param name="Categories">The categories they are in.</param>
public sealed record MemberProfile(
    Guid Id,
    string Name,
    string? DisplayName,
    string? Email,
    string? Bio,
    string Visibility,
    DateTimeOffset CreatedAt,
    bool IsDisabled,
    DateTimeOffset? PhotoUpdatedAt,
    IReadOnlyList<Guid> Categories);

/// <summary>What a member, or an administrator, changes on a profile.</summary>
/// <param name="DisplayName">What they are shown as; null or blank for the name.</param>
/// <param name="Email">Their e-mail address; null or blank for none.</param>
/// <param name="Bio">What they say about themselves; null or blank for nothing.</param>
/// <param name="Visibility"><c>private</c>, <c>organization</c> or <c>public</c>.</param>
public sealed record ProfileEdit(string? DisplayName, string? Email, string? Bio, string Visibility);

/// <summary>A member's photo.</summary>
/// <param name="ContentType">Its media type.</param>
/// <param name="Content">Its bytes.</param>
/// <param name="UpdatedAt">When it was set.</param>
public sealed record MemberPhoto(string ContentType, byte[] Content, DateTimeOffset UpdatedAt);

/// <summary>A category members are sorted into.</summary>
/// <param name="Id">Its id.</param>
/// <param name="ParentId">The category it sits under, or null at the top.</param>
/// <param name="Name">Its name.</param>
/// <param name="Members">How many members are in it directly.</param>
public sealed record MemberCategory(Guid Id, Guid? ParentId, string Name, int Members);

/// <summary>The rules a profile is held to, in one place for the store and the surface.</summary>
public static class MemberProfileRules
{
    /// <summary>Who may read a profile: only its member and administrators, anybody signed in, or anybody.</summary>
    /// <remarks>
    /// Not a sharing scope: a profile is not shared with a group, so there is no fourth.
    /// enum-default-is-deliberate: private
    /// </remarks>
    public static readonly IReadOnlyList<string> Visibilities = ["private", "organization", "public"];

    /// <summary>The largest photo kept, in bytes. Portal asks for 200 × 200; a megabyte is far past that.</summary>
    public const int LargestPhoto = 1_048_576;

    /// <summary>The longest bio, in characters.</summary>
    public const int LongestBio = 4_000;

    /// <summary>The longest category name, in characters.</summary>
    public const int LongestCategory = 100;

    /// <summary>The photo types kept: what Portal's upload accepts.</summary>
    public static readonly IReadOnlyList<string> PhotoTypes = ["image/png", "image/jpeg", "image/gif"];
}
