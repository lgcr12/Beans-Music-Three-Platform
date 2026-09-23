using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ.Dto;

internal sealed record QqRankingsEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqRankingsDataDto? Data,
    [property: JsonPropertyName("topList")] IReadOnlyList<QqRankingDto>? RootTopList);

internal sealed record QqRankingsDataDto(
    [property: JsonPropertyName("topList")] IReadOnlyList<QqRankingDto>? TopList);

internal sealed record QqRankingDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("topTitle")] string? TopTitle,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("subTitle")] string? SubTitle,
    [property: JsonPropertyName("updateTips")] string? UpdateTips,
    [property: JsonPropertyName("picUrl")] string? PicUrl,
    [property: JsonPropertyName("songList")] IReadOnlyList<QqRankingSongDto>? SongList);

internal sealed record QqRankingSongDto(
    [property: JsonPropertyName("songname")] string? SongName,
    [property: JsonPropertyName("singername")] string? SingerName);

internal sealed record QqRecommendEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("req_1")] QqRecommendRequestDto? Request);

internal sealed record QqRecommendRequestDto(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqRecommendDataDto? Data);

internal sealed record QqRecommendDataDto(
    [property: JsonPropertyName("v_playlist")] IReadOnlyList<QqPlaylistDto>? Playlists);

internal sealed record QqPlaylistSquareEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqPlaylistSquareDataDto? Data);

internal sealed record QqPlaylistSquareDataDto(
    [property: JsonPropertyName("list")] IReadOnlyList<QqPlaylistSquareItemDto>? Items);

internal sealed record QqPlaylistSquareItemDto(
    [property: JsonPropertyName("dissid")] string? DissId,
    [property: JsonPropertyName("dissname")] string? Name,
    [property: JsonPropertyName("imgurl")] string? ImageUrl,
    [property: JsonPropertyName("songnum")] int? SongCount,
    [property: JsonPropertyName("listennum")] long? PlayCount,
    [property: JsonPropertyName("creator")] QqPlaylistSquareCreatorDto? Creator);

internal sealed record QqPlaylistSquareCreatorDto(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record QqPlaylistDto(
    [property: JsonPropertyName("tid")] long? Tid,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("cover")] string? Cover,
    [property: JsonPropertyName("pic_url")] string? PictureUrl,
    [property: JsonPropertyName("songnum")] int? SongCount,
    [property: JsonPropertyName("access_num")] long? PlayCount,
    [property: JsonPropertyName("creator_info")] QqCreatorDto? Creator,
    [property: JsonPropertyName("username")] string? UserName);

internal sealed record QqCreatorDto(
    [property: JsonPropertyName("nick")] string? Nick,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record QqRankingsPayload(IReadOnlyList<QqRankingDto> Items);
internal sealed record QqPlaylistsPayload(IReadOnlyList<QqPlaylistDto> Items);
