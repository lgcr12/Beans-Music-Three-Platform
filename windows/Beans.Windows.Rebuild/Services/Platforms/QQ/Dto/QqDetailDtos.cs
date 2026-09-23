using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ.Dto;

internal sealed record QqPlaylistDetailEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("cdlist")] IReadOnlyList<QqPlaylistDetailDto>? Playlists);

internal sealed record QqPlaylistDetailDto(
    [property: JsonPropertyName("disstid")] string? Id,
    [property: JsonPropertyName("dissname")] string? Name,
    [property: JsonPropertyName("desc")] string? Description,
    [property: JsonPropertyName("logo")] string? Logo,
    [property: JsonPropertyName("creator")] QqDetailCreatorDto? Creator,
    [property: JsonPropertyName("songlist")] IReadOnlyList<QqDetailTrackDto>? Tracks);

internal sealed record QqRankingDetailEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("topinfo")] QqRankingInfoDto? TopInfo,
    [property: JsonPropertyName("songlist")] IReadOnlyList<QqRankingTrackWrapperDto>? Songs);

internal sealed record QqRankingInfoDto(
    [property: JsonPropertyName("ListName")] string? ListName,
    [property: JsonPropertyName("info")] string? Description,
    [property: JsonPropertyName("pic_album")] string? AlbumPicture,
    [property: JsonPropertyName("pic_v12")] string? AlternatePicture);

internal sealed record QqRankingTrackWrapperDto(
    [property: JsonPropertyName("data")] QqDetailTrackDto? Data,
    [property: JsonPropertyName("songInfo")] QqDetailTrackDto? SongInfo);

internal sealed record QqSingerSongsEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("singerSongList")] QqSingerSongsResponseDto? SingerSongList);

internal sealed record QqSingerSongsResponseDto(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("data")] QqSingerSongsDataDto? Data);

internal sealed record QqSingerSongsDataDto(
    [property: JsonPropertyName("singer_mid")] string? SingerMid,
    [property: JsonPropertyName("singerMid")] string? AlternateSingerMid,
    [property: JsonPropertyName("songList")] IReadOnlyList<QqSingerTrackWrapperDto>? Songs);

internal sealed record QqSingerTrackWrapperDto(
    [property: JsonPropertyName("songInfo")] QqDetailTrackDto? SongInfo);

internal sealed record QqDetailCreatorDto([property: JsonPropertyName("name")] string? Name);

internal sealed record QqDetailSingerDto(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record QqDetailAlbumDto(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record QqDetailFileDto(
    [property: JsonPropertyName("media_mid")] string? MediaMid,
    [property: JsonPropertyName("size_128mp3")] long? Size128,
    [property: JsonPropertyName("size_320mp3")] long? Size320,
    [property: JsonPropertyName("size_ape")] long? SizeApe,
    [property: JsonPropertyName("size_flac")] long? SizeFlac);

internal sealed record QqDetailTrackDto(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("songmid")] string? SongMid,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("songname")] string? SongName,
    [property: JsonPropertyName("singer")] IReadOnlyList<QqDetailSingerDto>? Singers,
    [property: JsonPropertyName("album")] QqDetailAlbumDto? Album,
    [property: JsonPropertyName("albummid")] string? AlbumMid,
    [property: JsonPropertyName("albumname")] string? AlbumName,
    [property: JsonPropertyName("interval")] int? DurationSeconds,
    [property: JsonPropertyName("file")] QqDetailFileDto? File);
