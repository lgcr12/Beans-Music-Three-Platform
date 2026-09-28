# Third-Party Notices

The repository root `LICENSE` is the controlling MIT license for Beans Music and must
remain with source or substantial redistributed portions. The repository README also
identifies the upstream XIaodou0416/Beans-Music project and its retained notice.

## Anime typography

Klee One Regular is bundled at `Assets/Fonts/KleeOne/KleeOne-Regular.ttf` from
https://github.com/google/fonts/tree/main/ofl/kleeone (Fontworks Inc.).
It is distributed under the SIL Open Font License 1.1; the complete license and
copyright notice are included alongside the font as `OFL.txt`.

## Runtime dependencies

- Microsoft Windows App SDK: Microsoft open-source components, distributed under the
  license declared by its NuGet packages.
- Microsoft.Extensions.DependencyInjection and Microsoft.Extensions.Logging.Debug:
  Microsoft open-source components, distributed under the license declared by their
  NuGet packages.

The packaged NuGet license metadata is authoritative for the exact restored versions.

## Preview images

The Home preview uses local copies of photographs delivered by Pexels under the
[Pexels license](https://www.pexels.com/license/). Source asset pages:

- https://www.pexels.com/photo/417074/
- https://www.pexels.com/photo/1001682/
- https://www.pexels.com/photo/302899/
- https://www.pexels.com/photo/325185/
- https://www.pexels.com/photo/1671325/
- https://www.pexels.com/photo/35537/

These files are visual PreviewData only. Platform-provided album and playlist artwork
must later retain its platform origin and be cached only as allowed by the applicable
service terms.

## Brand asset and preview audio

The Beans icon is copied from this repository's MIT-licensed `design/icon` source.
`preview-tone.wav` is an original procedural test tone generated for this rebuild and
contains no commercial recording.

QQ Music, NetEase Cloud Music, and KuGou Music names and marks belong to their respective
owners. Future platform logos may be used only to identify the selected source; this app
must not present itself as an official client.

## Anime catalog metadata and remote artwork

`Assets/Anime/catalog-metadata.json` contains public subject metadata retrieved from
the Bangumi API (`https://api.bgm.tv/v0/subjects/{id}`). Source subjects and validation
are recorded in `Docs/anime-rebuild-validation.md`. Poster URLs retain the Bangumi
`lain.bgm.tv` origin. Artwork and descriptions belong to their respective rights
holders; the project MIT license does not grant rights to those assets. Album covers
returned by music providers likewise retain their provider origin. Existing local
ambient illustrations are used as decorative scenes, not as official anime posters.
