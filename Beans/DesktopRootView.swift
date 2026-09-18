import SwiftUI

#if targetEnvironment(macCatalyst)

private enum DesktopSection: String, CaseIterable, Identifiable {
    case home
    case search
    case library
    case playlists
    case localMusic
    case downloads
    case profile

    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: return "主页"
        case .search: return "搜索"
        case .library: return "音乐库"
        case .playlists: return "我的歌单"
        case .localMusic: return "本地音乐"
        case .downloads: return "下载管理"
        case .profile: return "账号与设置"
        }
    }

    var icon: String {
        switch self {
        case .home: return "house.fill"
        case .search: return "magnifyingglass"
        case .library: return "music.note.list"
        case .playlists: return "rectangle.stack.fill"
        case .localMusic: return "folder.fill"
        case .downloads: return "arrow.down.circle.fill"
        case .profile: return "person.crop.circle.fill"
        }
    }

    var rootTab: RootTab {
        switch self {
        case .home: return .discover
        case .search: return .search
        case .library, .playlists, .localMusic, .downloads: return .library
        case .profile: return .profile
        }
    }
}

private struct DesktopPalette {
    let background: Color
    let backgroundSecondary: Color
    let sidebar: Color
    let surface: Color
    let elevatedSurface: Color
    let border: Color
    let primaryText: Color
    let secondaryText: Color
    let accent: Color
    let accentSecondary: Color
    let player: Color
    let shadow: Color
    let isLight: Bool

    init(style: BeansReferenceStyle) {
        switch style {
        case .aurora:
            background = Color(red: 0.025, green: 0.155, blue: 0.145)
            backgroundSecondary = Color(red: 0.055, green: 0.245, blue: 0.215)
            sidebar = Color(red: 0.025, green: 0.125, blue: 0.120).opacity(0.90)
            surface = Color.white.opacity(0.075)
            elevatedSurface = Color.white.opacity(0.12)
            border = Color.white.opacity(0.14)
            primaryText = .white
            secondaryText = Color.white.opacity(0.64)
            accent = Color(red: 0.31, green: 0.88, blue: 0.68)
            accentSecondary = Color(red: 0.92, green: 0.68, blue: 0.30)
            player = Color(red: 0.025, green: 0.16, blue: 0.145).opacity(0.94)
            shadow = .black.opacity(0.28)
            isLight = false
        case .paper:
            background = Color(red: 0.965, green: 0.975, blue: 0.972)
            backgroundSecondary = .white
            sidebar = Color(red: 0.925, green: 0.955, blue: 0.945)
            surface = .white
            elevatedSurface = Color(red: 0.955, green: 0.970, blue: 0.965)
            border = Color.black.opacity(0.075)
            primaryText = Color(red: 0.07, green: 0.11, blue: 0.10)
            secondaryText = Color(red: 0.39, green: 0.44, blue: 0.42)
            accent = Color(red: 0.06, green: 0.52, blue: 0.31)
            accentSecondary = Color(red: 0.14, green: 0.38, blue: 0.30)
            player = .white
            shadow = .black.opacity(0.10)
            isLight = true
        case .midnight:
            background = Color(red: 0.008, green: 0.020, blue: 0.034)
            backgroundSecondary = Color(red: 0.018, green: 0.045, blue: 0.070)
            sidebar = Color(red: 0.012, green: 0.032, blue: 0.050).opacity(0.97)
            surface = Color(red: 0.030, green: 0.070, blue: 0.105).opacity(0.86)
            elevatedSurface = Color(red: 0.045, green: 0.095, blue: 0.135)
            border = Color(red: 0.15, green: 0.64, blue: 0.86).opacity(0.22)
            primaryText = Color(red: 0.91, green: 0.96, blue: 1.0)
            secondaryText = Color(red: 0.50, green: 0.61, blue: 0.70)
            accent = Color(red: 0.0, green: 0.78, blue: 0.96)
            accentSecondary = Color(red: 0.96, green: 0.19, blue: 0.57)
            player = Color(red: 0.012, green: 0.035, blue: 0.055).opacity(0.98)
            shadow = Color(red: 0.0, green: 0.65, blue: 0.95).opacity(0.10)
            isLight = false
        }
    }
}

struct DesktopRootView: View {
    @EnvironmentObject private var theme: ThemeStore
    @EnvironmentObject private var player: PlayerManager
    @EnvironmentObject private var auth: AuthStore
    @EnvironmentObject private var favorites: FavoritesStore
    @ObservedObject private var account = BeansAccountStore.shared
    @ObservedObject private var localLibrary = LocalLibraryStore.shared

    @Binding var selection: RootTab
    @Binding var showPlayer: Bool

    @State private var desktopSelection: DesktopSection = .home
    @State private var rightPanel = DesktopRightPanel.queue

    private enum DesktopRightPanel: String, CaseIterable {
        case queue = "队列"
        case lyrics = "歌词"
    }

    private var palette: DesktopPalette { DesktopPalette(style: theme.referenceStyle) }

    var body: some View {
        GeometryReader { proxy in
            let compactSidebar = proxy.size.width < 900
            let showsRightPanel = proxy.size.width >= 1120

            ZStack {
                desktopBackground

                VStack(spacing: 0) {
                    HStack(spacing: 0) {
                        sidebar(compact: compactSidebar)
                            .frame(width: compactSidebar ? 78 : 218)

                        Divider().overlay(palette.border)

                        VStack(spacing: 0) {
                            desktopToolbar
                            Divider().overlay(palette.border)
                            content
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .clipped()

                        if showsRightPanel {
                            Divider().overlay(palette.border)
                            rightSidebar
                                .frame(width: 286)
                        }
                    }

                    Divider().overlay(palette.border)
                    DesktopPlayerBar(showPlayer: $showPlayer, palette: palette)
                        .frame(height: 84)
                }
            }
            .foregroundStyle(palette.primaryText)
            .tint(palette.accent)
            .animation(.easeInOut(duration: 0.22), value: theme.referenceStyle)
            .animation(.easeInOut(duration: 0.20), value: compactSidebar)
        }
        .ignoresSafeArea(.container, edges: .bottom)
        .onChange(of: desktopSelection) { newValue in
            selection = newValue.rootTab
        }
        .environmentObject(player.clock)
    }

    private var desktopBackground: some View {
        ZStack {
            palette.background

            if theme.referenceStyle == .aurora {
                LinearGradient(
                    colors: [
                        Color(red: 0.02, green: 0.26, blue: 0.22),
                        palette.background,
                        Color(red: 0.18, green: 0.12, blue: 0.055)
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
                if let image = theme.customBackgroundImage {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                        .opacity(0.28)
                        .blur(radius: 2)
                }
                BeansParticleCanvas(
                    accent: palette.accent,
                    secondary: palette.accentSecondary,
                    isPlaying: player.isPlaying,
                    intensity: 0.44
                )
                .opacity(0.48)
            } else if theme.referenceStyle == .midnight {
                LinearGradient(
                    colors: [palette.background, palette.backgroundSecondary, .black],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
            }
        }
        .ignoresSafeArea()
    }

    private func sidebar(compact: Bool) -> some View {
        VStack(alignment: compact ? .center : .leading, spacing: 0) {
            HStack(spacing: 11) {
                Image("OnboardingLogo")
                    .resizable()
                    .scaledToFill()
                    .frame(width: 32, height: 32)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                if !compact {
                    Text("Beans Music")
                        .font(BeansFont.appFont(16, .bold))
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: compact ? .center : .leading)
            .padding(.horizontal, compact ? 0 : 18)
            .frame(height: 64)

            VStack(spacing: 5) {
                sidebarButton(.home, compact: compact)
                sidebarButton(.search, compact: compact)
                sidebarButton(.library, compact: compact)
                sidebarButton(.playlists, compact: compact)
                sidebarButton(.localMusic, compact: compact)
                sidebarButton(.downloads, compact: compact)
            }
            .padding(.horizontal, compact ? 10 : 12)

            if !compact {
                Text("我的音乐")
                    .font(BeansFont.appFont(11, .semibold))
                    .foregroundStyle(palette.secondaryText)
                    .padding(.horizontal, 18)
                    .padding(.top, 24)
                    .padding(.bottom, 8)

                ScrollView {
                    VStack(spacing: 3) {
                        ForEach(localLibrary.playlists.prefix(6)) { playlist in
                            Button {
                                desktopSelection = .playlists
                            } label: {
                                HStack(spacing: 9) {
                                    Image(systemName: "music.note")
                                        .font(.system(size: 11, weight: .medium))
                                        .foregroundStyle(palette.accent)
                                        .frame(width: 18)
                                    Text(playlist.name)
                                        .font(BeansFont.appFont(12))
                                        .lineLimit(1)
                                    Spacer(minLength: 0)
                                    Text("\(playlist.songs.count)")
                                        .font(BeansFont.appFont(10, .medium, .monospaced))
                                        .foregroundStyle(palette.secondaryText)
                                }
                                .foregroundStyle(palette.primaryText.opacity(0.86))
                                .padding(.horizontal, 8)
                                .frame(height: 30)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                .beansScrollIndicatorsHidden()
                .padding(.horizontal, 12)
            }

            Spacer(minLength: 12)

            sidebarButton(.profile, compact: compact)
                .padding(.horizontal, compact ? 10 : 12)
                .padding(.bottom, 12)

            HStack(spacing: 9) {
                Circle()
                    .fill(account.isSignedIn ? palette.accent : palette.secondaryText)
                    .frame(width: 8, height: 8)
                if !compact {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(account.account?.nickname ?? "未登录 Beans")
                            .font(BeansFont.appFont(11, .semibold))
                            .lineLimit(1)
                        Text(account.isSignedIn ? "云端同步已开启" : "登录后跨端同步")
                            .font(BeansFont.appFont(9))
                            .foregroundStyle(palette.secondaryText)
                            .lineLimit(1)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: compact ? .center : .leading)
            .padding(.horizontal, compact ? 0 : 19)
            .frame(height: 52)
            .background(palette.surface)
        }
        .background(palette.sidebar)
    }

    private func sidebarButton(_ item: DesktopSection, compact: Bool) -> some View {
        let selected = desktopSelection == item
        return Button {
            desktopSelection = item
        } label: {
            HStack(spacing: 11) {
                Image(systemName: item.icon)
                    .font(.system(size: 14, weight: selected ? .semibold : .regular))
                    .frame(width: 22)
                if !compact {
                    Text(item.title)
                        .font(BeansFont.appFont(13, selected ? .semibold : .regular))
                        .lineLimit(1)
                    Spacer(minLength: 0)
                }
            }
            .foregroundStyle(selected ? (palette.isLight ? .white : palette.primaryText) : palette.primaryText.opacity(0.72))
            .frame(maxWidth: .infinity, alignment: compact ? .center : .leading)
            .frame(height: 38)
            .padding(.horizontal, compact ? 0 : 10)
            .background {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(selected ? palette.accent.opacity(palette.isLight ? 0.88 : 0.18) : .clear)
                    .overlay {
                        if selected && !palette.isLight {
                            RoundedRectangle(cornerRadius: 7, style: .continuous)
                                .strokeBorder(palette.accent.opacity(0.20), lineWidth: 1)
                        }
                    }
            }
            .contentShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
        }
        .buttonStyle(.plain)
        .help(item.title)
        .accessibilityLabel(item.title)
    }

    private var desktopToolbar: some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 2) {
                Text(desktopSelection.title)
                    .font(BeansFont.appFont(18, .bold))
                Text(toolbarSubtitle)
                    .font(BeansFont.appFont(10))
                    .foregroundStyle(palette.secondaryText)
            }

            Spacer(minLength: 12)

            HStack(spacing: 4) {
                ForEach(BeansReferenceStyle.allCases) { style in
                    Button {
                        theme.setReferenceStyle(style)
                    } label: {
                        Image(systemName: style.icon)
                            .font(.system(size: 12, weight: .semibold))
                            .frame(width: 30, height: 28)
                            .foregroundStyle(theme.referenceStyle == style ? (palette.isLight ? .white : palette.primaryText) : palette.secondaryText)
                            .background(
                                RoundedRectangle(cornerRadius: 6, style: .continuous)
                                    .fill(theme.referenceStyle == style ? palette.accent : .clear)
                            )
                    }
                    .buttonStyle(.plain)
                    .help(style.title)
                    .accessibilityLabel("切换到\(style.title)")
                }
            }
            .padding(3)
            .background(palette.surface, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(palette.border, lineWidth: 1)
            }

            Button {
                account.markSynced()
            } label: {
                Image(systemName: account.isSyncing ? "arrow.triangle.2.circlepath" : "icloud.and.arrow.up")
                    .font(.system(size: 13, weight: .semibold))
                    .frame(width: 32, height: 32)
                    .background(palette.surface, in: Circle())
            }
            .buttonStyle(.plain)
            .help(account.isSignedIn ? "立即同步" : "请先登录 Beans 账号")
            .disabled(!account.isSignedIn || account.isSyncing)
            .accessibilityLabel("立即同步")
        }
        .padding(.horizontal, 20)
        .frame(height: 64)
        .background(palette.background.opacity(0.72))
    }

    private var toolbarSubtitle: String {
        switch desktopSelection {
        case .home: return "跨平台音乐，一处管理"
        case .search: return "搜索 QQ 音乐、网易云与酷狗"
        case .library: return "收藏、历史与平台歌单"
        case .playlists: return "Beans 自建歌单将跨端同步"
        case .localMusic: return "管理这台设备上的音乐"
        case .downloads: return "查看已保存与正在下载的内容"
        case .profile: return "Beans 账号、授权、设备与外观"
        }
    }

    @ViewBuilder
    private var content: some View {
        switch desktopSelection {
        case .home:
            DesktopHomeView(palette: palette, showPlayer: $showPlayer)
        case .search:
            SearchView()
        case .library:
            LibraryView()
        case .playlists:
            DesktopPlaylistsView(palette: palette)
        case .localMusic:
            LibraryView()
        case .downloads:
            DesktopDownloadsView(palette: palette)
        case .profile:
            ProfileView()
        }
    }

    private var rightSidebar: some View {
        VStack(spacing: 0) {
            HStack(spacing: 4) {
                ForEach(DesktopRightPanel.allCases, id: \.rawValue) { panel in
                    Button {
                        rightPanel = panel
                    } label: {
                        Text(panel.rawValue)
                            .font(BeansFont.appFont(12, rightPanel == panel ? .semibold : .regular))
                            .foregroundStyle(rightPanel == panel ? palette.primaryText : palette.secondaryText)
                            .frame(maxWidth: .infinity)
                            .frame(height: 30)
                            .background {
                                if rightPanel == panel {
                                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                                        .fill(palette.elevatedSurface)
                                }
                            }
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(6)
            .background(palette.surface, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .padding(14)

            if rightPanel == .queue {
                DesktopQueuePanel(palette: palette)
            } else {
                DesktopLyricsPanel(palette: palette)
            }
        }
        .background(palette.sidebar.opacity(0.88))
    }
}

private struct DesktopHomeView: View {
    @EnvironmentObject private var theme: ThemeStore
    @EnvironmentObject private var player: PlayerManager
    @EnvironmentObject private var favorites: FavoritesStore
    @ObservedObject private var localLibrary = LocalLibraryStore.shared

    let palette: DesktopPalette
    @Binding var showPlayer: Bool

    private var recentSongs: [Song] { Array(player.history.prefix(8)) }
    private var favoriteSongs: [Song] {
        Array((favorites.neteaseFavoriteSongs + favorites.qqFavoriteSongs).prefix(6))
    }

    var body: some View {
        switch theme.referenceStyle {
        case .aurora:
            auroraHome
        case .paper:
            paperHome
        case .midnight:
            midnightHome
        }
    }

    private var auroraHome: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                hero(style: .aurora)
                coverSection(title: "为你推荐", songs: displaySongs, cardSize: 132)
                compactRecentSection(title: "最近播放", songs: recentSongs)
            }
            .padding(24)
        }
        .beansScrollIndicatorsHidden()
    }

    private var paperHome: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .top, spacing: 26) {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("推荐歌单")
                            .font(BeansFont.appFont(19, .bold))
                        coverGrid(songs: displaySongs, columns: 4, cardSize: 118)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    VStack(alignment: .leading, spacing: 14) {
                        Text("本地音乐")
                            .font(BeansFont.appFont(19, .bold))
                        paperLibrarySummary
                    }
                    .frame(width: 230)
                }

                HStack(alignment: .top, spacing: 26) {
                    compactRecentSection(title: "最近播放", songs: recentSongs)
                    paperPlaylistSummary
                        .frame(width: 230)
                }
            }
            .padding(24)
        }
        .beansScrollIndicatorsHidden()
    }

    private var midnightHome: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                hero(style: .midnight)
                HStack(alignment: .top, spacing: 22) {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("最近播放")
                            .font(BeansFont.appFont(18, .bold))
                        DesktopSongTable(songs: recentSongs, palette: palette, maximumRows: 7)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    VStack(alignment: .leading, spacing: 14) {
                        Text("云音乐库")
                            .font(BeansFont.appFont(18, .bold))
                        cloudStats
                    }
                    .frame(width: 230)
                }
            }
            .padding(24)
        }
        .beansScrollIndicatorsHidden()
    }

    private enum HeroStyle { case aurora, midnight }

    private func hero(style: HeroStyle) -> some View {
        Button {
            if player.currentSong != nil { showPlayer = true }
        } label: {
            ZStack {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: style == .aurora
                                ? [Color(red: 0.05, green: 0.36, blue: 0.29), Color(red: 0.15, green: 0.20, blue: 0.13)]
                                : [Color(red: 0.02, green: 0.12, blue: 0.22), Color(red: 0.14, green: 0.03, blue: 0.17)],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )

                if let url = player.currentSong?.coverURL {
                    AsyncImage(url: url) { phase in
                        if case .success(let image) = phase {
                            image
                                .resizable()
                                .scaledToFill()
                                .opacity(0.30)
                                .blur(radius: 8)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                }

                HStack(spacing: 22) {
                    if style == .midnight {
                        CoverImage(url: player.currentSong?.coverURL, size: 132, cornerRadius: 8)
                    }

                    VStack(alignment: .leading, spacing: 9) {
                        Text(style == .aurora ? "早上好" : (player.currentSong?.name ?? "午夜电台"))
                            .font(BeansFont.appFont(style == .aurora ? 30 : 26, .bold))
                        Text(style == .aurora ? "让音乐陪你开始今天" : (player.currentSong?.artists ?? "选择一首歌，进入沉浸播放舞台"))
                            .font(BeansFont.appFont(13))
                            .foregroundStyle(Color.white.opacity(0.72))
                        HStack(spacing: 10) {
                            Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                                .font(.system(size: 14, weight: .bold))
                                .foregroundStyle(style == .aurora ? Color(red: 0.03, green: 0.25, blue: 0.19) : .white)
                                .frame(width: 38, height: 38)
                                .background(style == .aurora ? palette.accent : palette.accentSecondary, in: Circle())
                            if style == .midnight {
                                DesktopSpectrum(accent: palette.accent, secondary: palette.accentSecondary)
                                    .frame(width: 220, height: 42)
                            }
                        }
                    }
                    Spacer(minLength: 0)
                }
                .padding(24)
            }
            .frame(height: style == .aurora ? 185 : 210)
            .overlay {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(palette.border, lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(player.currentSong == nil ? "尚未播放音乐" : "打开完整播放器")
    }

    private var displaySongs: [Song] {
        let all = favoriteSongs + recentSongs
        var seen = Set<String>()
        return all.filter { seen.insert($0.identityKey).inserted }.prefix(8).map { $0 }
    }

    private func coverSection(title: String, songs: [Song], cardSize: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 13) {
            Text(title)
                .font(BeansFont.appFont(18, .bold))
            coverGrid(songs: songs, columns: 5, cardSize: cardSize)
        }
    }

    private func coverGrid(songs: [Song], columns: Int, cardSize: CGFloat) -> some View {
        Group {
            if songs.isEmpty {
                emptyLibraryHint
            } else {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16), count: columns), spacing: 16) {
                    ForEach(songs, id: \.identityKey) { song in
                        Button {
                            player.playSong(song, in: songs)
                        } label: {
                            VStack(alignment: .leading, spacing: 7) {
                                CoverImage(url: song.coverURL, size: cardSize, cornerRadius: palette.isLight ? 5 : 8)
                                    .frame(maxWidth: .infinity)
                                Text(song.name)
                                    .font(BeansFont.appFont(12, .semibold))
                                    .foregroundStyle(palette.primaryText)
                                    .lineLimit(1)
                                Text(song.artists)
                                    .font(BeansFont.appFont(10))
                                    .foregroundStyle(palette.secondaryText)
                                    .lineLimit(1)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private func compactRecentSection(title: String, songs: [Song]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(BeansFont.appFont(18, .bold))
            DesktopSongTable(songs: songs, palette: palette, maximumRows: 6)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var paperLibrarySummary: some View {
        VStack(spacing: 0) {
            paperSummaryRow(icon: "music.note", title: "收藏歌曲", count: favorites.neteaseFavoriteSongs.count + favorites.qqFavoriteSongs.count)
            paperSummaryRow(icon: "clock.arrow.circlepath", title: "最近播放", count: player.history.count)
            paperSummaryRow(icon: "rectangle.stack", title: "本地歌单", count: localLibrary.playlists.count)
        }
        .background(palette.surface)
        .overlay { RoundedRectangle(cornerRadius: 6).strokeBorder(palette.border, lineWidth: 1) }
    }

    private func paperSummaryRow(icon: String, title: String, count: Int) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .foregroundStyle(palette.accent)
                .frame(width: 22)
            Text(title).font(BeansFont.appFont(12, .medium))
            Spacer()
            Text("\(count) 首")
                .font(BeansFont.appFont(10))
                .foregroundStyle(palette.secondaryText)
        }
        .padding(.horizontal, 12)
        .frame(height: 44)
        .overlay(alignment: .bottom) { Divider().overlay(palette.border) }
    }

    private var paperPlaylistSummary: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("我的歌单")
                .font(BeansFont.appFont(18, .bold))
            VStack(spacing: 0) {
                ForEach(localLibrary.playlists.prefix(5)) { playlist in
                    HStack(spacing: 9) {
                        Image(systemName: "music.note.list")
                            .foregroundStyle(palette.accent)
                        Text(playlist.name)
                            .font(BeansFont.appFont(12, .medium))
                            .lineLimit(1)
                        Spacer()
                        Text("\(playlist.songs.count)")
                            .font(BeansFont.appFont(10))
                            .foregroundStyle(palette.secondaryText)
                    }
                    .padding(.horizontal, 12)
                    .frame(height: 42)
                    .overlay(alignment: .bottom) { Divider().overlay(palette.border) }
                }
                if localLibrary.playlists.isEmpty {
                    Text("还没有自建歌单")
                        .font(BeansFont.appFont(11))
                        .foregroundStyle(palette.secondaryText)
                        .frame(maxWidth: .infinity)
                        .frame(height: 80)
                }
            }
            .background(palette.surface)
            .overlay { RoundedRectangle(cornerRadius: 6).strokeBorder(palette.border, lineWidth: 1) }
        }
    }

    private var cloudStats: some View {
        VStack(spacing: 0) {
            paperSummaryRow(icon: "music.note", title: "全部歌曲", count: player.history.count + favorites.neteaseFavoriteSongs.count + favorites.qqFavoriteSongs.count)
            paperSummaryRow(icon: "heart", title: "收藏", count: favorites.neteaseFavoriteSongs.count + favorites.qqFavoriteSongs.count)
            paperSummaryRow(icon: "rectangle.stack", title: "歌单", count: localLibrary.playlists.count)
            paperSummaryRow(icon: "arrow.down.circle", title: "已下载", count: 0)
        }
        .background(palette.surface, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(palette.border, lineWidth: 1) }
    }

    private var emptyLibraryHint: some View {
        VStack(spacing: 8) {
            Image(systemName: "music.note.house")
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(palette.accent)
            Text("播放或收藏音乐后，这里会生成你的推荐")
                .font(BeansFont.appFont(12, .medium))
            Text("搜索 QQ 音乐或网易云音乐即可开始")
                .font(BeansFont.appFont(10))
                .foregroundStyle(palette.secondaryText)
        }
        .frame(maxWidth: .infinity)
        .frame(height: 132)
        .background(palette.surface, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(palette.border, lineWidth: 1) }
    }
}

private struct DesktopSongTable: View {
    @EnvironmentObject private var player: PlayerManager
    let songs: [Song]
    let palette: DesktopPalette
    let maximumRows: Int

    var body: some View {
        VStack(spacing: 0) {
            if songs.isEmpty {
                Text("暂无播放记录")
                    .font(BeansFont.appFont(11))
                    .foregroundStyle(palette.secondaryText)
                    .frame(maxWidth: .infinity)
                    .frame(height: 96)
            } else {
                HStack(spacing: 10) {
                    Text("#").frame(width: 22)
                    Text("标题").frame(maxWidth: .infinity, alignment: .leading)
                    Text("歌手").frame(width: 130, alignment: .leading)
                    Text("时长").frame(width: 50, alignment: .trailing)
                }
                .font(BeansFont.appFont(9, .semibold))
                .foregroundStyle(palette.secondaryText)
                .padding(.horizontal, 10)
                .frame(height: 28)

                ForEach(Array(songs.prefix(maximumRows).enumerated()), id: \.element.identityKey) { index, song in
                    Button {
                        player.playSong(song, in: songs)
                    } label: {
                        HStack(spacing: 10) {
                            Text("\(index + 1)")
                                .frame(width: 22)
                            CoverImage(url: song.coverURL, size: 30, cornerRadius: 5)
                            Text(song.name)
                                .font(BeansFont.appFont(11, .semibold))
                                .lineLimit(1)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            Text(song.artists)
                                .font(BeansFont.appFont(10))
                                .foregroundStyle(palette.secondaryText)
                                .lineLimit(1)
                                .frame(width: 130, alignment: .leading)
                            Text(song.formattedDuration)
                                .font(BeansFont.appFont(9, .regular, .monospaced))
                                .foregroundStyle(palette.secondaryText)
                                .frame(width: 50, alignment: .trailing)
                        }
                        .foregroundStyle(player.currentSong?.identityKey == song.identityKey ? palette.accent : palette.primaryText)
                        .padding(.horizontal, 10)
                        .frame(height: 43)
                        .background(index.isMultiple(of: 2) ? palette.surface.opacity(0.60) : .clear)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .background(palette.surface.opacity(palette.isLight ? 1 : 0.66))
        .overlay { RoundedRectangle(cornerRadius: 7).strokeBorder(palette.border, lineWidth: 1) }
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
    }
}

private struct DesktopQueuePanel: View {
    @EnvironmentObject private var player: PlayerManager
    let palette: DesktopPalette

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("播放队列")
                    .font(BeansFont.appFont(14, .bold))
                Spacer()
                Text("\(player.queue.count) 首")
                    .font(BeansFont.appFont(10, .medium))
                    .foregroundStyle(palette.secondaryText)
                Button {
                    player.clearQueue()
                } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(palette.secondaryText)
                }
                .buttonStyle(.plain)
                .disabled(player.queue.isEmpty)
                .help("清空队列")
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 10)

            if player.queue.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "music.note.list")
                        .font(.system(size: 24, weight: .light))
                    Text("播放队列为空")
                        .font(BeansFont.appFont(11))
                }
                .foregroundStyle(palette.secondaryText)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 3) {
                        ForEach(Array(player.queue.enumerated()), id: \.element.identityKey) { index, song in
                            Button {
                                player.playQueueIndex(index)
                            } label: {
                                HStack(spacing: 9) {
                                    CoverImage(url: song.coverURL, size: 36, cornerRadius: 6)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(song.name)
                                            .font(BeansFont.appFont(11, index == player.currentIndex ? .semibold : .regular))
                                            .lineLimit(1)
                                        Text(song.artists)
                                            .font(BeansFont.appFont(9))
                                            .foregroundStyle(palette.secondaryText)
                                            .lineLimit(1)
                                    }
                                    Spacer(minLength: 2)
                                    if index == player.currentIndex {
                                        NowPlayingIndicator()
                                    } else {
                                        Text(song.formattedDuration)
                                            .font(BeansFont.appFont(9, .regular, .monospaced))
                                            .foregroundStyle(palette.secondaryText)
                                    }
                                }
                                .foregroundStyle(index == player.currentIndex ? palette.accent : palette.primaryText)
                                .padding(.horizontal, 9)
                                .frame(height: 51)
                                .background(index == player.currentIndex ? palette.elevatedSurface : .clear, in: RoundedRectangle(cornerRadius: 6))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.bottom, 12)
                }
                .beansScrollIndicatorsHidden()
            }
        }
        .frame(maxHeight: .infinity)
    }
}

private struct DesktopLyricsPanel: View {
    @EnvironmentObject private var player: PlayerManager
    @EnvironmentObject private var clock: PlaybackClock
    let palette: DesktopPalette
    @State private var lines: [LyricLine] = []

    private var activeIndex: Int {
        guard !lines.isEmpty else { return 0 }
        return lines.lastIndex(where: { $0.time <= clock.progress }) ?? 0
    }

    var body: some View {
        Group {
            if player.currentSong == nil {
                VStack(spacing: 10) {
                    Image(systemName: "quote.bubble")
                        .font(.system(size: 23, weight: .light))
                    Text("开始播放后显示歌词")
                        .font(BeansFont.appFont(11))
                }
                .foregroundStyle(palette.secondaryText)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if lines.isEmpty {
                ProgressView("正在加载歌词")
                    .font(BeansFont.appFont(11))
                    .tint(palette.accent)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 16) {
                            ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                                Text(line.text)
                                    .font(BeansFont.appFont(index == activeIndex ? 15 : 12, index == activeIndex ? .bold : .regular))
                                    .foregroundStyle(index == activeIndex ? palette.accent : palette.secondaryText)
                                    .id(index)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(18)
                    }
                    .beansScrollIndicatorsHidden()
                    .onChange(of: activeIndex) { index in
                        withAnimation(.easeInOut(duration: 0.25)) { proxy.scrollTo(index, anchor: .center) }
                    }
                }
            }
        }
        .task(id: player.currentSong?.identityKey) { await loadLyrics() }
    }

    private func loadLyrics() async {
        lines = []
        guard let song = player.currentSong else { return }
        let identity = song.identityKey
        let raw: String?
        if song.source == .qq, let mid = song.qqMid {
            raw = try? await QQMusicAPI.shared.lyric(songmid: mid)
        } else if song.source == .kugou, let hash = song.kugouHash {
            raw = await KugouMusicAPI.shared.lyric(hash: hash, duration: song.duration)
        } else {
            raw = try? await NetEaseAPI.shared.lyric(id: song.id)
        }
        guard player.currentSong?.identityKey == identity, let raw else { return }
        lines = LyricParser.parse(raw)
    }
}

private struct DesktopPlayerBar: View {
    @EnvironmentObject private var player: PlayerManager
    @EnvironmentObject private var clock: PlaybackClock
    @EnvironmentObject private var favorites: FavoritesStore
    @Binding var showPlayer: Bool
    let palette: DesktopPalette

    var body: some View {
        HStack(spacing: 18) {
            Button {
                if player.currentSong != nil { showPlayer = true }
            } label: {
                HStack(spacing: 11) {
                    CoverImage(url: player.currentSong?.coverURL, size: 50, cornerRadius: palette.isLight ? 5 : 8)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(player.currentSong?.name ?? "尚未播放")
                            .font(BeansFont.appFont(12, .semibold))
                            .foregroundStyle(palette.primaryText)
                            .lineLimit(1)
                        Text(player.currentSong?.artists ?? "从主页或搜索选择一首歌")
                            .font(BeansFont.appFont(10))
                            .foregroundStyle(palette.secondaryText)
                            .lineLimit(1)
                    }
                    .frame(width: 145, alignment: .leading)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            Button {
                guard let song = player.currentSong else { return }
                Task { _ = await favorites.toggle(song) }
            } label: {
                Image(systemName: favorites.isLiked(player.currentSong) ? "heart.fill" : "heart")
                    .foregroundStyle(favorites.isLiked(player.currentSong) ? palette.accentSecondary : palette.secondaryText)
                    .frame(width: 28, height: 28)
            }
            .buttonStyle(.plain)
            .disabled(player.currentSong == nil)
            .help("收藏")

            Spacer(minLength: 8)

            HStack(spacing: 15) {
                Button { player.previous() } label: {
                    Image(systemName: "backward.fill")
                        .frame(width: 30, height: 30)
                }
                Button { player.togglePlayPause() } label: {
                    Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(palette.isLight ? .white : palette.primaryText)
                        .frame(width: 38, height: 38)
                        .background(palette.accent, in: Circle())
                }
                Button { player.next() } label: {
                    Image(systemName: "forward.fill")
                        .frame(width: 30, height: 30)
                }
            }
            .foregroundStyle(palette.primaryText)
            .buttonStyle(.plain)

            HStack(spacing: 9) {
                Text(beansTimeString(clock.progress))
                    .frame(width: 42, alignment: .trailing)
                Slider(
                    value: Binding(
                        get: { min(clock.progress, max(clock.duration, 1)) },
                        set: { player.seek(to: $0) }
                    ),
                    in: 0...max(clock.duration, 1)
                )
                .tint(palette.accent)
                Text(beansTimeString(clock.duration))
                    .frame(width: 42, alignment: .leading)
            }
            .font(BeansFont.appFont(9, .regular, .monospaced))
            .foregroundStyle(palette.secondaryText)
            .frame(maxWidth: 390)

            Spacer(minLength: 8)

            Button { player.togglePlayMode() } label: {
                Image(systemName: player.playMode.icon)
                    .frame(width: 30, height: 30)
            }
            .buttonStyle(.plain)
            .foregroundStyle(palette.secondaryText)
            .help("切换播放模式")

            Button { showPlayer = true } label: {
                Image(systemName: "arrow.up.left.and.arrow.down.right")
                    .frame(width: 30, height: 30)
            }
            .buttonStyle(.plain)
            .foregroundStyle(palette.secondaryText)
            .disabled(player.currentSong == nil)
            .help("打开完整播放器")
        }
        .padding(.horizontal, 18)
        .background(palette.player)
        .shadow(color: palette.shadow, radius: 12, y: -3)
    }
}

private struct DesktopPlaylistsView: View {
    @EnvironmentObject private var player: PlayerManager
    @ObservedObject private var localLibrary = LocalLibraryStore.shared
    let palette: DesktopPalette

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Beans 自建歌单")
                            .font(BeansFont.appFont(22, .bold))
                        Text("登录 Beans 账号后会加密同步到其他设备")
                            .font(BeansFont.appFont(11))
                            .foregroundStyle(palette.secondaryText)
                    }
                    Spacer()
                    Button {
                        _ = localLibrary.createPlaylist(name: "新建歌单")
                    } label: {
                        Label("新建歌单", systemImage: "plus")
                            .font(BeansFont.appFont(12, .semibold))
                            .padding(.horizontal, 13)
                            .frame(height: 34)
                            .foregroundStyle(palette.isLight ? .white : palette.primaryText)
                            .background(palette.accent, in: RoundedRectangle(cornerRadius: 7))
                    }
                    .buttonStyle(.plain)
                }

                if localLibrary.playlists.isEmpty {
                    VStack(spacing: 10) {
                        Image(systemName: "rectangle.stack.badge.plus")
                            .font(.system(size: 30, weight: .light))
                            .foregroundStyle(palette.accent)
                        Text("还没有自建歌单")
                            .font(BeansFont.appFont(13, .semibold))
                        Text("新建歌单后可在 iPhone、Mac 和 Windows 之间同步")
                            .font(BeansFont.appFont(10))
                            .foregroundStyle(palette.secondaryText)
                    }
                    .frame(maxWidth: .infinity)
                    .frame(height: 220)
                    .background(palette.surface, in: RoundedRectangle(cornerRadius: 8))
                    .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(palette.border, lineWidth: 1) }
                } else {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 210), spacing: 14)], spacing: 14) {
                        ForEach(localLibrary.playlists) { playlist in
                            Button {
                                if !playlist.songs.isEmpty { player.play(songs: playlist.songs) }
                            } label: {
                                HStack(spacing: 12) {
                                    CoverImage(url: playlist.songs.first?.coverURL, size: 58, cornerRadius: 7)
                                    VStack(alignment: .leading, spacing: 5) {
                                        Text(playlist.name)
                                            .font(BeansFont.appFont(13, .semibold))
                                            .foregroundStyle(palette.primaryText)
                                            .lineLimit(1)
                                        Text("\(playlist.songs.count) 首歌曲")
                                            .font(BeansFont.appFont(10))
                                            .foregroundStyle(palette.secondaryText)
                                    }
                                    Spacer(minLength: 0)
                                    Image(systemName: "play.fill")
                                        .font(.system(size: 11, weight: .bold))
                                        .foregroundStyle(palette.accent)
                                }
                                .padding(10)
                                .background(palette.surface, in: RoundedRectangle(cornerRadius: 8))
                                .overlay { RoundedRectangle(cornerRadius: 8).strokeBorder(palette.border, lineWidth: 1) }
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            .padding(24)
        }
        .beansScrollIndicatorsHidden()
    }
}

private struct DesktopDownloadsView: View {
    let palette: DesktopPalette

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "arrow.down.circle")
                .font(.system(size: 32, weight: .light))
                .foregroundStyle(palette.accent)
            Text("下载内容保存在本机")
                .font(BeansFont.appFont(15, .semibold))
            Text("离线音频不会上传或跨端同步，可从歌曲菜单发起下载。")
                .font(BeansFont.appFont(11))
                .foregroundStyle(palette.secondaryText)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct DesktopSpectrum: View {
    let accent: Color
    let secondary: Color

    var body: some View {
        HStack(alignment: .center, spacing: 2) {
            ForEach(0..<42, id: \.self) { index in
                Capsule()
                    .fill(
                        LinearGradient(
                            colors: [accent, secondary],
                            startPoint: .bottom,
                            endPoint: .top
                        )
                    )
                    .frame(width: 3, height: CGFloat(8 + ((index * 17) % 31)))
            }
        }
        .accessibilityHidden(true)
    }
}

#endif
