using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;

internal sealed record NetEaseTopListEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("list")] IReadOnlyList<NetEaseRankingDto>? List);

internal sealed record NetEaseRankingDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("coverImgUrl")] string? CoverImgUrl,
    [property: JsonPropertyName("updateFrequency")] string? UpdateFrequency,
    [property: JsonPropertyName("tracks")] IReadOnlyList<NetEaseTrackDto>? Tracks);

internal sealed record NetEaseTrackDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record NetEasePlaylistEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("playlists")] IReadOnlyList<NetEasePlaylistDto>? Playlists);

internal sealed record NetEasePlaylistDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("coverImgUrl")] string? CoverImgUrl,
    [property: JsonPropertyName("trackCount")] int? TrackCount,
    [property: JsonPropertyName("playCount")] long? PlayCount,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("creator")] NetEaseCreatorDto? Creator);

internal sealed record NetEaseCreatorDto(
    [property: JsonPropertyName("nickname")] string? Nickname);

internal sealed record NetEaseCategoryEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("sub")] IReadOnlyList<NetEaseCategoryDto>? Sub);

internal sealed record NetEaseCategoryDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("category")] int? Category);

internal sealed record NetEaseRankingsPayload(IReadOnlyList<NetEaseRankingDto> Items);
internal sealed record NetEasePlaylistsPayload(IReadOnlyList<NetEasePlaylistDto> Items);
internal sealed record NetEaseCategoriesPayload(IReadOnlyList<string> Items);
