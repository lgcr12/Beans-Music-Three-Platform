import SwiftUI

struct MusicUniverseSnapshot {
    let currentSong: Song?
    let recentSongs: [Song]
    let totalPlays: Int
    let favoriteCount: Int
    let topSong: Song?

    static func make(player: PlayerManager, favorites: FavoritesStore) -> MusicUniverseSnapshot {
        let favoriteSongs = favorites.neteaseFavoriteSongs + favorites.qqFavoriteSongs
        var seen = Set<String>()
        let recent = (player.history + favoriteSongs).filter { seen.insert($0.identityKey).inserted }.prefix(12)
        let topKey = player.playCounts.max { lhs, rhs in lhs.value < rhs.value }?.key
        let candidates = player.history + favoriteSongs + player.queue
        return MusicUniverseSnapshot(
            currentSong: player.currentSong,
            recentSongs: Array(recent),
            totalPlays: player.playCounts.values.reduce(0, +),
            favoriteCount: Set(favoriteSongs.map(\.identityKey)).count,
            topSong: topKey.flatMap { key in candidates.first(where: { $0.identityKey == key }) }
        )
    }
}

struct MusicUniverseView: View {
    @EnvironmentObject private var theme: ThemeStore
    @EnvironmentObject private var player: PlayerManager
    @EnvironmentObject private var favorites: FavoritesStore
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    let openPlayer: () -> Void
    let openHistory: () -> Void

    private var snapshot: MusicUniverseSnapshot {
        MusicUniverseSnapshot.make(player: player, favorites: favorites)
    }

    private var heroHeight: CGFloat {
        #if targetEnvironment(macCatalyst)
        310
        #else
        280
        #endif
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("音乐宇宙")
                        .font(BeansFont.appFont(22, .bold))
                        .foregroundStyle(Color.beansLabel)
                    Text("你的播放、收藏与最近心动")
                        .font(BeansFont.appFont(12))
                        .foregroundStyle(Color.beansComment)
                }
                Spacer()
                Image(systemName: theme.referenceStyle.icon)
                    .font(.system(size: 20, weight: .semibold))
                    .foregroundStyle(Color.beansAmber)
                    .accessibilityHidden(true)
            }

            universeHero
            statsStrip

            if snapshot.recentSongs.isEmpty {
                emptyUniverse
            } else {
                coverWall
            }
        }
    }

    private var universeHero: some View {
        ZStack {
                heroBackground
                if theme.referenceStyle == .aurora {
                    BeansParticleCanvas(
                        accent: Color.beansAmber,
                        secondary: Color.beansHighlight,
                        isPlaying: player.isPlaying && !reduceMotion,
                        intensity: 0.56
                    )
                    .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
                }

                if let song = snapshot.currentSong {
                    currentSongContent(song)
                } else {
                    VStack(spacing: 12) {
                        Image(systemName: "waveform.circle.fill")
                            .font(.system(size: 48, weight: .light))
                            .foregroundStyle(Color.beansAmber)
                        Text("等待第一首歌")
                            .font(BeansFont.appFont(22, .bold))
                            .foregroundStyle(Color.beansLabel)
                        Text("开始播放后，这里会变成你的专属音乐舞台")
                            .font(BeansFont.appFont(13))
                            .foregroundStyle(Color.beansComment)
                            .multilineTextAlignment(.center)
                    }
                    .padding(24)
                }
        }
        .frame(maxWidth: .infinity)
        .frame(minHeight: heroHeight)
        .fixedSize(horizontal: false, vertical: true)
        .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
        .overlay {
            if theme.referenceStyle == .midnight {
                RoundedRectangle(cornerRadius: 24, style: .continuous)
                    .stroke(Color.beansHighlight.opacity(0.45), lineWidth: 1)
            }
        }
        .contentShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
        .onTapGesture {
            if snapshot.currentSong != nil { openPlayer() }
        }
        .accessibilityLabel(snapshot.currentSong == nil ? "尚未播放" : "打开当前歌曲播放器")
    }

    @ViewBuilder
    private var heroBackground: some View {
        switch theme.referenceStyle {
        case .aurora:
            LinearGradient(
                colors: [Color(red: 0.035, green: 0.25, blue: 0.22), Color(red: 0.05, green: 0.13, blue: 0.14), Color(red: 0.32, green: 0.22, blue: 0.08)],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
        case .paper:
            Color(uiColor: .secondarySystemBackground)
        case .midnight:
            LinearGradient(
                colors: [Color(red: 0.015, green: 0.03, blue: 0.075), Color(red: 0.02, green: 0.08, blue: 0.14)],
                startPoint: .top,
                endPoint: .bottomTrailing
            )
        }
    }

    private func currentSongContent(_ song: Song) -> some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 24) {
                CoverImage(url: song.coverURL, size: 170, cornerRadius: theme.referenceStyle == .paper ? 10 : 22)
                    .rotationEffect(.degrees(reduceMotion || !player.isPlaying ? 0 : 1.5))
                    .animation(.easeInOut(duration: 1.8).repeatForever(autoreverses: true), value: player.isPlaying)
                nowPlayingDetails(song, alignment: .leading)
            }
            .padding(26)

            VStack(spacing: 16) {
                CoverImage(url: song.coverURL, size: 142, cornerRadius: theme.referenceStyle == .paper ? 10 : 22)
                nowPlayingDetails(song, alignment: .center)
            }
            .padding(22)
        }
    }

    private func nowPlayingDetails(_ song: Song, alignment: HorizontalAlignment) -> some View {
        VStack(alignment: alignment, spacing: 8) {
            Text(player.isPlaying ? "正在播放" : "已暂停")
                .font(BeansFont.appFont(11, .bold))
                .foregroundStyle(Color.beansAmber)
            Text(song.name)
                .font(BeansFont.appFont(24, .bold))
                .foregroundStyle(Color.beansLabel)
                .lineLimit(2)
                .multilineTextAlignment(alignment == .center ? .center : .leading)
            Text(song.artists.isEmpty ? song.album : song.artists)
                .font(BeansFont.appFont(13))
                .foregroundStyle(Color.beansComment)
                .lineLimit(2)
                .multilineTextAlignment(alignment == .center ? .center : .leading)
            HStack(spacing: 5) {
                ForEach(0..<8, id: \.self) { index in
                    Capsule()
                        .fill(spectrumColor(index))
                        .frame(width: 4, height: spectrumHeight(index))
                }
            }
            .frame(height: 36, alignment: .bottom)
            .accessibilityHidden(true)
            Button {
                player.togglePlayPause()
            } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundStyle(theme.referenceStyle == .paper ? Color.white : Color.black.opacity(0.78))
                    .frame(width: 46, height: 46)
                    .background(Color.beansAmber, in: Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(player.isPlaying ? "暂停" : "播放")
        }
        .frame(maxWidth: .infinity, alignment: alignment == .center ? .center : .leading)
    }

    private func spectrumHeight(_ index: Int) -> CGFloat {
        guard player.isPlaying, !reduceMotion else { return CGFloat(10 + (index % 3) * 5) }
        return CGFloat([15, 27, 35, 22, 32, 18, 29, 13][index])
    }

    private func spectrumColor(_ index: Int) -> Color {
        guard theme.referenceStyle == .midnight else { return index.isMultiple(of: 3) ? Color.beansAmber : Color.beansHighlight }
        return [Color.cyan, .blue, .purple, .pink][index % 4]
    }

    private var statsStrip: some View {
        Group {
            if dynamicTypeSize.isAccessibilitySize || TextScaleStore.shared.percent >= 125 {
                VStack(spacing: 10) {
                    statButtons
                }
            } else {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 10) {
                        statButtons
                    }
                    VStack(spacing: 10) {
                        statButtons
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var statButtons: some View {
        statButton(icon: "play.circle.fill", value: "\(snapshot.totalPlays)", label: "累计播放", action: openHistory)
        statButton(icon: "heart.fill", value: "\(snapshot.favoriteCount)", label: "收藏歌曲") {
            NotificationCenter.default.post(name: .beansOpenRootTab, object: RootTab.library.rawValue)
        }
        statButton(
            icon: "crown.fill",
            value: snapshot.topSong?.name ?? "暂无",
            label: "最常播放"
        ) {
            guard let song = snapshot.topSong,
                  let index = player.history.firstIndex(where: { $0.identityKey == song.identityKey }) else { return }
            player.play(songs: player.history, startAt: index)
        }
    }

    private func statButton(icon: String, value: String, label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 6) {
                Image(systemName: icon)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Color.beansAmber)
                Text(value)
                    .font(BeansFont.appFont(15, .bold))
                    .foregroundStyle(Color.beansLabel)
                    .lineLimit(2)
                Text(label)
                    .font(BeansFont.appFont(10))
                    .foregroundStyle(Color.beansComment)
                    .lineLimit(2)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background {
                BeansGlass(shape: RoundedRectangle(cornerRadius: theme.referenceStyle == .paper ? 8 : 16, style: .continuous))
            }
        }
        .buttonStyle(GlassPressButtonStyle(scale: 0.97))
    }

    private var coverWall: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("最近心动")
                    .font(BeansFont.appFont(16, .bold))
                    .foregroundStyle(Color.beansLabel)
                Spacer()
                Button("全部历史", action: openHistory)
                    .font(BeansFont.appFont(12, .semibold))
                    .foregroundStyle(Color.beansAmber)
            }
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(spacing: 12) {
                    ForEach(snapshot.recentSongs, id: \.identityKey) { song in
                        Button {
                            guard let index = snapshot.recentSongs.firstIndex(where: { $0.identityKey == song.identityKey }) else { return }
                            player.play(songs: snapshot.recentSongs, startAt: index)
                        } label: {
                            VStack(alignment: .leading, spacing: 7) {
                                CoverImage(url: song.coverURL, size: 112, cornerRadius: theme.referenceStyle == .paper ? 7 : 15)
                                Text(song.name)
                                    .font(BeansFont.appFont(12, .semibold))
                                    .foregroundStyle(Color.beansLabel)
                                    .lineLimit(1)
                                Text(song.artists.isEmpty ? song.album : song.artists)
                                    .font(BeansFont.appFont(10))
                                    .foregroundStyle(Color.beansComment)
                                    .lineLimit(1)
                            }
                            .frame(width: 112, alignment: .leading)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.vertical, 2)
            }
        }
    }

    private var emptyUniverse: some View {
        VStack(spacing: 12) {
            Text("从一首歌开始建立你的音乐宇宙")
                .font(BeansFont.appFont(15, .semibold))
                .foregroundStyle(Color.beansLabel)
                .multilineTextAlignment(.center)
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 10) {
                    emptyAction("去听一首", icon: "sparkles", tab: .discover)
                    emptyAction("导入本地音乐", icon: "folder.badge.plus", tab: .library)
                }
                VStack(spacing: 10) {
                    emptyAction("去听一首", icon: "sparkles", tab: .discover)
                    emptyAction("导入本地音乐", icon: "folder.badge.plus", tab: .library)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(18)
        .background {
            BeansGlass(shape: RoundedRectangle(cornerRadius: theme.referenceStyle == .paper ? 8 : 18, style: .continuous))
        }
    }

    private func emptyAction(_ title: String, icon: String, tab: RootTab) -> some View {
        Button {
            NotificationCenter.default.post(name: .beansOpenRootTab, object: tab.rawValue)
        } label: {
            Label(title, systemImage: icon)
                .font(BeansFont.appFont(12, .semibold))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 10)
        }
        .buttonStyle(.bordered)
        .tint(Color.beansAmber)
    }
}
