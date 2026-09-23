using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ.Dto.Search;

internal sealed record QqClientSearchEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqClientSearchDataDto? Data);

internal sealed record QqClientSearchDataDto(
    [property: JsonPropertyName("song")] QqTrackSearchBlockDto? Song,
    [property: JsonPropertyName("album")] QqAlbumSearchBlockDto? Album);

internal sealed record QqTrackSearchBlockDto(
    [property: JsonPropertyName("curnum")] int? CurrentCount,
    [property: JsonPropertyName("curpage")] int? CurrentPage,
    [property: JsonPropertyName("totalnum")] int? TotalCount,
    [property: JsonPropertyName("list")] IReadOnlyList<QqTrackSearchItemDto>? Items);

internal sealed record QqTrackSearchItemDto(
    [property: JsonPropertyName("songid")] long? SongId,
    [property: JsonPropertyName("songmid")] string? SongMid,
    [property: JsonPropertyName("songname")] string? SongName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("singer")] IReadOnlyList<QqSingerDto>? Singers,
    [property: JsonPropertyName("albummid")] string? AlbumMid,
    [property: JsonPropertyName("albumname")] string? AlbumName,
    [property: JsonPropertyName("interval")] int? DurationSeconds,
    [property: JsonPropertyName("fee")] int? Fee,
    [property: JsonPropertyName("pay")] QqTrackPayDto? Pay,
    [property: JsonPropertyName("file")] QqTrackFileDto? File);

internal sealed record QqSingerDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record QqTrackPayDto(
    [property: JsonPropertyName("pay_play")] int? PayPlay,
    [property: JsonPropertyName("payplay")] int? AlternatePayPlay);

internal sealed record QqTrackFileDto(
    [property: JsonPropertyName("media_mid")] string? MediaMid,
    [property: JsonPropertyName("size_128mp3")] long? Size128,
    [property: JsonPropertyName("size_320mp3")] long? Size320,
    [property: JsonPropertyName("size_ape")] long? SizeApe,
    [property: JsonPropertyName("size_flac")] long? SizeFlac);

internal sealed record QqAlbumSearchBlockDto(
    [property: JsonPropertyName("curnum")] int? CurrentCount,
    [property: JsonPropertyName("curpage")] int? CurrentPage,
    [property: JsonPropertyName("totalnum")] int? TotalCount,
    [property: JsonPropertyName("list")] IReadOnlyList<QqAlbumSearchItemDto>? Items);

internal sealed record QqAlbumSearchItemDto(
    [property: JsonPropertyName("albumid")] long? AlbumId,
    [property: JsonPropertyName("albummid")] string? AlbumMid,
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("albumname")] string? AlbumName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("singer")] IReadOnlyList<QqSingerDto>? Singers,
    [property: JsonPropertyName("singerName")] string? SingerName,
    [property: JsonPropertyName("publictime")] string? PublicTime,
    [property: JsonPropertyName("song_count")] int? SongCount,
    [property: JsonPropertyName("total")] int? AlternateSongCount);

internal sealed record QqSmartboxEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqSmartboxDataDto? Data);

internal sealed record QqSmartboxDataDto(
    [property: JsonPropertyName("singer")] QqSmartboxGroupDto? Singers,
    [property: JsonPropertyName("album")] QqSmartboxGroupDto? Albums,
    [property: JsonPropertyName("song")] QqSmartboxGroupDto? Songs);

internal sealed record QqSmartboxGroupDto(
    [property: JsonPropertyName("count")] int? Count,
    [property: JsonPropertyName("itemlist")] IReadOnlyList<QqSmartboxItemDto>? Items);

internal sealed record QqSmartboxItemDto(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("singer")] string? Singer,
    [property: JsonPropertyName("pic")] string? Picture);

internal sealed record QqTrackSearchPayload(QqTrackSearchBlockDto Page);
internal sealed record QqAlbumSearchPayload(QqAlbumSearchBlockDto Page);
internal sealed record QqArtistSearchPayload(QqSmartboxGroupDto Group);
internal sealed record QqSuggestionPayload(QqSmartboxDataDto Data);
