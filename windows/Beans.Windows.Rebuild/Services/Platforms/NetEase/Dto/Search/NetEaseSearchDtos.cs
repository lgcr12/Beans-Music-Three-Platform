using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto.Search;

internal sealed record NetEaseTrackSearchResponse(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("result")] NetEaseTrackSearchResultDto? Result);

internal sealed record NetEaseTrackSearchResultDto(
    [property: JsonPropertyName("songCount")] int? TotalCount,
    [property: JsonPropertyName("songs")] IReadOnlyList<NetEaseSearchTrackDto>? Songs);

internal sealed record NetEaseSearchTrackDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("artists")] IReadOnlyList<NetEaseSearchArtistRefDto>? Artists,
    [property: JsonPropertyName("ar")] IReadOnlyList<NetEaseSearchArtistRefDto>? AlternateArtists,
    [property: JsonPropertyName("album")] NetEaseSearchAlbumRefDto? Album,
    [property: JsonPropertyName("al")] NetEaseSearchAlbumRefDto? AlternateAlbum,
    [property: JsonPropertyName("duration")] long? DurationMilliseconds,
    [property: JsonPropertyName("dt")] long? AlternateDurationMilliseconds,
    [property: JsonPropertyName("fee")] int? Fee,
    [property: JsonPropertyName("h")] NetEaseSearchQualityDto? High,
    [property: JsonPropertyName("m")] NetEaseSearchQualityDto? Medium,
    [property: JsonPropertyName("l")] NetEaseSearchQualityDto? Low,
    [property: JsonPropertyName("sq")] NetEaseSearchQualityDto? Lossless);

internal sealed record NetEaseSearchArtistRefDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record NetEaseSearchAlbumRefDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picUrl")] string? PictureUrl,
    [property: JsonPropertyName("blurPicUrl")] string? BlurPictureUrl);

internal sealed record NetEaseSearchQualityDto(
    [property: JsonPropertyName("br")] int? Bitrate,
    [property: JsonPropertyName("size")] long? Size);

internal sealed record NetEaseAlbumSearchResponse(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("result")] NetEaseAlbumSearchResultDto? Result);

internal sealed record NetEaseAlbumSearchResultDto(
    [property: JsonPropertyName("albumCount")] int? TotalCount,
    [property: JsonPropertyName("albums")] IReadOnlyList<NetEaseSearchAlbumDto>? Albums);

internal sealed record NetEaseSearchAlbumDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picUrl")] string? PictureUrl,
    [property: JsonPropertyName("artist")] NetEaseSearchArtistRefDto? Artist,
    [property: JsonPropertyName("artists")] IReadOnlyList<NetEaseSearchArtistRefDto>? Artists,
    [property: JsonPropertyName("publishTime")] long? PublishTime,
    [property: JsonPropertyName("size")] int? TrackCount);

internal sealed record NetEaseArtistSearchResponse(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("result")] NetEaseArtistSearchResultDto? Result);

internal sealed record NetEaseArtistSearchResultDto(
    [property: JsonPropertyName("artistCount")] int? TotalCount,
    [property: JsonPropertyName("artists")] IReadOnlyList<NetEaseSearchArtistDto>? Artists);

internal sealed record NetEaseSearchArtistDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picUrl")] string? PictureUrl,
    [property: JsonPropertyName("img1v1Url")] string? AvatarUrl,
    [property: JsonPropertyName("briefDesc")] string? BriefDescription,
    [property: JsonPropertyName("alias")] IReadOnlyList<string>? Aliases);

internal sealed record NetEaseTrackSearchPayload(NetEaseTrackSearchResultDto Result);
internal sealed record NetEaseAlbumSearchPayload(NetEaseAlbumSearchResultDto Result);
internal sealed record NetEaseArtistSearchPayload(NetEaseArtistSearchResultDto Result);
