using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;

internal sealed record NetEasePlaylistDetailEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("playlist")] NetEasePlaylistDetailDto? Playlist);

internal sealed record NetEasePlaylistDetailDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("coverImgUrl")] string? CoverImageUrl,
    [property: JsonPropertyName("creator")] NetEaseDetailCreatorDto? Creator,
    [property: JsonPropertyName("tracks")] IReadOnlyList<NetEaseDetailTrackDto>? Tracks);

internal sealed record NetEaseAlbumDetailEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("album")] NetEaseAlbumDetailDto? Album,
    [property: JsonPropertyName("songs")] IReadOnlyList<NetEaseDetailTrackDto>? Songs);

internal sealed record NetEaseAlbumDetailDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("picUrl")] string? PictureUrl,
    [property: JsonPropertyName("publishTime")] long? PublishTime,
    [property: JsonPropertyName("artist")] NetEaseDetailArtistRefDto? Artist);

internal sealed record NetEaseArtistDetailEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("artist")] NetEaseArtistDetailDto? Artist,
    [property: JsonPropertyName("hotSongs")] IReadOnlyList<NetEaseDetailTrackDto>? HotSongs);

internal sealed record NetEaseArtistDetailDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("briefDesc")] string? BriefDescription,
    [property: JsonPropertyName("picUrl")] string? PictureUrl,
    [property: JsonPropertyName("img1v1Url")] string? AvatarUrl);

internal sealed record NetEaseDetailCreatorDto([property: JsonPropertyName("nickname")] string? Nickname);

internal sealed record NetEaseDetailArtistRefDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record NetEaseDetailAlbumRefDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picUrl")] string? PictureUrl);

internal sealed record NetEaseDetailQualityDto([property: JsonPropertyName("br")] int? Bitrate);

internal sealed record NetEaseDetailTrackDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("ar")] IReadOnlyList<NetEaseDetailArtistRefDto>? Artists,
    [property: JsonPropertyName("artists")] IReadOnlyList<NetEaseDetailArtistRefDto>? LegacyArtists,
    [property: JsonPropertyName("al")] NetEaseDetailAlbumRefDto? Album,
    [property: JsonPropertyName("album")] NetEaseDetailAlbumRefDto? LegacyAlbum,
    [property: JsonPropertyName("dt")] long? DurationMilliseconds,
    [property: JsonPropertyName("duration")] long? LegacyDurationMilliseconds,
    [property: JsonPropertyName("h")] NetEaseDetailQualityDto? High,
    [property: JsonPropertyName("m")] NetEaseDetailQualityDto? Medium,
    [property: JsonPropertyName("l")] NetEaseDetailQualityDto? Low,
    [property: JsonPropertyName("sq")] NetEaseDetailQualityDto? Lossless,
    [property: JsonPropertyName("fee")] int? Fee = null,
    [property: JsonPropertyName("privilege")] NetEaseDetailPrivilegeDto? Privilege = null,
    [property: JsonPropertyName("noCopyrightRcmd")] object? NoCopyrightReason = null);

internal sealed record NetEaseDetailPrivilegeDto([property: JsonPropertyName("st")] int? Status);

internal sealed record NetEaseDailyRecommendationsEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] NetEaseDailyRecommendationsDataDto? Data);

internal sealed record NetEaseDailyRecommendationsDataDto(
    [property: JsonPropertyName("dailySongs")] IReadOnlyList<NetEaseDetailTrackDto>? DailySongs);
