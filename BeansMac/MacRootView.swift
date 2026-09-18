import SwiftUI
import UniformTypeIdentifiers

private enum MacDestination: Hashable {
    case home
    case library
    case playlist(UUID)
}

struct MacRootView: View {
    @EnvironmentObject private var library: MacLibraryStore
    @EnvironmentObject private var player: MacPlayer
    @State private var destination: MacDestination? = .home
    @State private var searchText = ""
    @State private var isImporting = false
    @State private var isCreatingPlaylist = false
    @State private var playlistName = ""

    var body: some View {
        NavigationSplitView {
            sidebar
        } content: {
            content
        } detail: {
            MacNowPlayingPane()
                .frame(minWidth: 250, idealWidth: 280, maxWidth: 340)
        }
        .navigationSplitViewStyle(.balanced)
        .safeAreaInset(edge: .bottom, spacing: 0) {
            MacBottomPlayer()
        }
        .fileImporter(isPresented: $isImporting, allowedContentTypes: [.audio], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result else { return }
            library.importFiles(urls)
        }
        .alert("新建本地歌单", isPresented: $isCreatingPlaylist) {
            TextField("歌单名称", text: $playlistName)
            Button("创建") {
                if let playlist = library.createPlaylist(named: playlistName) {
                    destination = .playlist(playlist.id)
                }
                playlistName = ""
            }
            Button("取消", role: .cancel) { playlistName = "" }
        } message: {
            Text("歌单和歌曲索引只保存在这台 Mac 上。")
        }
        .onReceive(NotificationCenter.default.publisher(for: .beansMacImportAudio)) { _ in
            isImporting = true
        }
    }

    private var sidebar: some View {
        List(selection: $destination) {
            Section("浏览") {
                Label("发现", systemImage: "sparkles")
                    .tag(MacDestination.home)
                Label("本地音乐", systemImage: "music.note.list")
                    .tag(MacDestination.library)
            }

            Section("本地歌单") {
                ForEach(library.playlists) { playlist in
                    Label(playlist.name, systemImage: "music.note.list")
                        .tag(MacDestination.playlist(playlist.id))
                        .contextMenu {
                            Button(role: .destructive) {
                                if destination == .playlist(playlist.id) { destination = .library }
                                library.deletePlaylist(playlist.id)
                            } label: {
                                Label("删除歌单", systemImage: "trash")
                            }
                        }
                }
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("Beans Music")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button { isImporting = true } label: {
                        Label("导入本地音乐", systemImage: "square.and.arrow.down")
                    }
                    Button { isCreatingPlaylist = true } label: {
                        Label("新建歌单", systemImage: "plus")
                    }
                } label: {
                    Image(systemName: "plus")
                }
                .accessibilityLabel("添加音乐或歌单")
            }
        }
    }

    @ViewBuilder private var content: some View {
        switch destination ?? .home {
        case .home:
            MacHomeView(searchText: $searchText) { isImporting = true }
        case .library:
            MacLibraryView(searchText: $searchText) { isImporting = true }
        case .playlist(let id):
            MacPlaylistView(playlistID: id, searchText: $searchText)
        }
    }
}

struct MacHomeView: View {
    @EnvironmentObject private var library: MacLibraryStore
    @EnvironmentObject private var player: MacPlayer
    @Binding var searchText: String
    let importAction: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                HStack(alignment: .bottom) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("欢迎回来")
                            .font(.system(size: 30, weight: .bold, design: .rounded))
                        Text("把本地音乐整理成属于你的播放空间。")
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("导入音乐", systemImage: "square.and.arrow.down") { importAction() }
                        .buttonStyle(.borderedProminent)
                }

                HStack(spacing: 12) {
                    MacStat(label: "本地歌曲", value: "\(library.songs.count)", icon: "music.note")
                    MacStat(label: "本地歌单", value: "\(library.playlists.count)", icon: "music.note.list")
                    MacStat(label: "队列", value: "\(player.queue.count)", icon: "text.line.first.and.arrowtriangle.forward")
                }

                if library.songs.isEmpty {
                    MacEmptyLibrary(importAction: importAction)
                } else {
                    HStack {
                        Text("最近导入").font(.title2.weight(.bold))
                        Spacer()
                        TextField("搜索本地音乐", text: $searchText)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 250)
                    }
                    MacSongTable(songs: filteredSongs, context: filteredSongs)
                }
            }
            .padding(28)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle("发现")
    }

    private var filteredSongs: [MacSong] {
        let keyword = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return Array(library.songs.prefix(18)) }
        return library.songs.filter { song in
            song.title.localizedCaseInsensitiveContains(keyword) || song.artist.localizedCaseInsensitiveContains(keyword) || song.album.localizedCaseInsensitiveContains(keyword)
        }
    }
}

struct MacLibraryView: View {
    @EnvironmentObject private var library: MacLibraryStore
    @Binding var searchText: String
    let importAction: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 14) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("本地音乐").font(.system(size: 28, weight: .bold, design: .rounded))
                    Text("\(library.songs.count) 首歌曲 · 存储在这台 Mac")
                        .font(.subheadline).foregroundStyle(.secondary)
                }
                Spacer()
                TextField("搜索歌曲、歌手或专辑", text: $searchText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 260)
                Button("导入", systemImage: "square.and.arrow.down") { importAction() }
            }
            .padding(28)

            if library.songs.isEmpty {
                MacEmptyLibrary(importAction: importAction)
            } else {
                MacSongTable(songs: filteredSongs, context: filteredSongs)
                    .padding(.horizontal, 20)
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle("本地音乐")
    }

    private var filteredSongs: [MacSong] {
        let keyword = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return library.songs }
        return library.songs.filter { song in
            song.title.localizedCaseInsensitiveContains(keyword) || song.artist.localizedCaseInsensitiveContains(keyword) || song.album.localizedCaseInsensitiveContains(keyword)
        }
    }
}

struct MacPlaylistView: View {
    @EnvironmentObject private var library: MacLibraryStore
    @EnvironmentObject private var player: MacPlayer
    let playlistID: UUID
    @Binding var searchText: String
    @State private var renaming = false
    @State private var renamedValue = ""

    private var playlist: MacPlaylist? { library.playlists.first { $0.id == playlistID } }

    var body: some View {
        Group {
            if let playlist {
                let songs = library.songs(in: playlist)
                VStack(spacing: 0) {
                    HStack(alignment: .center, spacing: 16) {
                        Image(systemName: "music.note.list")
                            .font(.system(size: 34, weight: .medium))
                            .foregroundStyle(.white)
                            .frame(width: 78, height: 78)
                            .background(LinearGradient(colors: [.accentColor, .accentColor.opacity(0.65)], startPoint: .topLeading, endPoint: .bottomTrailing), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                        VStack(alignment: .leading, spacing: 5) {
                            Text(playlist.name).font(.system(size: 28, weight: .bold, design: .rounded))
                            Text("本地歌单 · \(songs.count) 首歌曲").foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("播放全部", systemImage: "play.fill") { player.play(songs) }
                            .buttonStyle(.borderedProminent)
                            .disabled(songs.isEmpty)
                        Button("随机播放", systemImage: "shuffle") { player.play(songs.shuffled()) }
                            .disabled(songs.isEmpty)
                        Menu {
                            Button("重命名") { renamedValue = playlist.name; renaming = true }
                            Button("删除歌单", role: .destructive) { library.deletePlaylist(playlist.id) }
                        } label: { Image(systemName: "ellipsis.circle") }
                    }
                    .padding(28)

                    if songs.isEmpty {
                        MacEmptyState(title: "歌单还是空的", systemImage: "music.note.list", detail: "在“本地音乐”里右键一首歌曲，然后加入这个歌单。")
                    } else {
                        MacSongTable(songs: filteredSongs(songs), context: songs, playlistID: playlist.id)
                            .padding(.horizontal, 20)
                    }
                }
                .alert("重命名歌单", isPresented: $renaming) {
                    TextField("歌单名称", text: $renamedValue)
                    Button("保存") { library.renamePlaylist(playlist.id, to: renamedValue) }
                    Button("取消", role: .cancel) {}
                }
            } else {
                MacEmptyState(title: "歌单不存在", systemImage: "music.note.list", detail: "这个本地歌单可能已被删除。")
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle(playlist?.name ?? "本地歌单")
    }

    private func filteredSongs(_ songs: [MacSong]) -> [MacSong] {
        let keyword = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return songs }
        return songs.filter { $0.title.localizedCaseInsensitiveContains(keyword) || $0.artist.localizedCaseInsensitiveContains(keyword) || $0.album.localizedCaseInsensitiveContains(keyword) }
    }
}

struct MacSongTable: View {
    @EnvironmentObject private var library: MacLibraryStore
    @EnvironmentObject private var player: MacPlayer
    let songs: [MacSong]
    let context: [MacSong]
    var playlistID: UUID?

    var body: some View {
        List {
            HStack(spacing: 14) {
                Text("#").frame(width: 28, alignment: .trailing)
                Text("歌曲").frame(maxWidth: .infinity, alignment: .leading)
                Text("专辑").frame(width: 170, alignment: .leading)
                Text("时长").frame(width: 48, alignment: .trailing)
                Color.clear.frame(width: 28)
            }
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .textCase(.uppercase)

            ForEach(Array(songs.enumerated()), id: \.element.id) { index, song in
                HStack(spacing: 14) {
                    Button { player.play(context, startingAt: context.firstIndex(of: song) ?? index) } label: {
                        Image(systemName: player.currentSong?.id == song.id && player.isPlaying ? "speaker.wave.2.fill" : "play.fill")
                            .font(.caption.weight(.bold))
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.borderless)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(song.title).lineLimit(1)
                        Text(song.artist).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    Text(song.album).foregroundStyle(.secondary).lineLimit(1).frame(width: 170, alignment: .leading)
                    Text(song.durationText).monospacedDigit().foregroundStyle(.secondary).frame(width: 48, alignment: .trailing)
                    Menu {
                        Button("立即播放", systemImage: "play.fill") { player.play(context, startingAt: context.firstIndex(of: song) ?? index) }
                        Divider()
                        if !library.playlists.isEmpty {
                            Menu("添加到本地歌单", systemImage: "text.badge.plus") {
                                ForEach(library.playlists) { playlist in
                                    Button(playlist.name) { library.add(song, to: playlist.id) }
                                }
                            }
                        }
                        if let playlistID {
                            Button("从此歌单移除", systemImage: "minus.circle", role: .destructive) {
                                library.remove(song.id, from: playlistID)
                            }
                        }
                        Button("从本地音乐库移除", systemImage: "trash", role: .destructive) {
                            library.deleteSong(song.id)
                        }
                    } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton)
                    .frame(width: 28)
                }
                .padding(.vertical, 4)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { player.play(context, startingAt: context.firstIndex(of: song) ?? index) }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("\(song.title)，\(song.artist)，时长 \(song.durationText)")
            }
        }
        .listStyle(.inset(alternatesRowBackgrounds: true))
    }
}

struct MacNowPlayingPane: View {
    @EnvironmentObject private var player: MacPlayer

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("正在播放").font(.headline)
            ZStack {
                RoundedRectangle(cornerRadius: 18, style: .continuous).fill(LinearGradient(colors: [.accentColor, .accentColor.opacity(0.65)], startPoint: .topLeading, endPoint: .bottomTrailing))
                Image(systemName: "music.note").font(.system(size: 52, weight: .light)).foregroundStyle(.white.opacity(0.92))
            }
            .aspectRatio(1, contentMode: .fit)
            VStack(alignment: .leading, spacing: 4) {
                Text(player.currentSong?.title ?? "还没有播放歌曲").font(.title3.weight(.semibold)).lineLimit(2)
                Text(player.currentSong?.artist ?? "从本地音乐库选择一首歌曲开始").font(.subheadline).foregroundStyle(.secondary).lineLimit(2)
            }
            Divider()
            HStack {
                Text("接下来播放").font(.headline)
                Spacer()
                Text("\(player.queue.count)").font(.caption).foregroundStyle(.secondary)
            }
            if player.queue.isEmpty {
                Spacer()
                MacEmptyState(title: "播放队列为空", systemImage: "text.line.first.and.arrowtriangle.forward", detail: "双击一首本地歌曲即可开始播放。")
                Spacer()
            } else {
                List(Array(player.queue.enumerated()), id: \.element.id) { index, song in
                    HStack(spacing: 8) {
                        Text("\(index + 1)").font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 18)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(song.title).lineLimit(1)
                            Text(song.artist).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .foregroundStyle(player.currentSong?.id == song.id ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.primary))
                }
                .listStyle(.plain)
            }
        }
        .padding(18)
        .background(.thinMaterial)
    }
}

struct MacBottomPlayer: View {
    @EnvironmentObject private var player: MacPlayer

    var body: some View {
        HStack(spacing: 18) {
            HStack(spacing: 10) {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(LinearGradient(colors: [.accentColor, .accentColor.opacity(0.65)], startPoint: .topLeading, endPoint: .bottomTrailing))
                    .frame(width: 42, height: 42)
                    .overlay(Image(systemName: "music.note").foregroundStyle(.white))
                VStack(alignment: .leading, spacing: 2) {
                    Text(player.currentSong?.title ?? "未选择歌曲").font(.subheadline.weight(.semibold)).lineLimit(1)
                    Text(player.currentSong?.artist ?? "本地音乐库").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                .frame(width: 190, alignment: .leading)
            }

            HStack(spacing: 12) {
                Button(action: player.cycleMode) { Image(systemName: player.playMode.icon) }
                    .help("切换播放模式")
                Button(action: player.previous) { Image(systemName: "backward.fill") }
                    .disabled(player.queue.isEmpty)
                Button(action: player.toggle) {
                    Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                        .frame(width: 30, height: 30)
                }
                .buttonStyle(.borderedProminent)
                .disabled(player.queue.isEmpty)
                Button(action: player.next) { Image(systemName: "forward.fill") }
                    .disabled(player.queue.isEmpty)
            }

            HStack(spacing: 8) {
                Text(time(player.currentTime)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 38, alignment: .trailing)
                Slider(value: Binding(get: { player.currentTime }, set: { player.seek(to: $0) }), in: 0...max(player.duration, 1))
                    .tint(.accentColor)
                Text(time(player.duration)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 38, alignment: .leading)
            }
            .frame(minWidth: 220, maxWidth: 520)

            Spacer(minLength: 0)
            HStack(spacing: 8) {
                Image(systemName: "speaker.wave.2").foregroundStyle(.secondary)
                Slider(value: $player.volume, in: 0...1).frame(width: 100)
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
        .background(.regularMaterial)
        .overlay(alignment: .top) { Divider() }
    }

    private func time(_ value: TimeInterval) -> String {
        let total = max(0, Int(value))
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

private struct MacStat: View {
    let label: String
    let value: String
    let icon: String

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon).foregroundStyle(.tint).frame(width: 28, height: 28).background(.tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 7))
            VStack(alignment: .leading, spacing: 1) {
                Text(value).font(.headline.monospacedDigit())
                Text(label).font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

private struct MacEmptyLibrary: View {
    let importAction: () -> Void

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "music.note.list").font(.system(size: 38)).foregroundStyle(.secondary)
            Text("导入你的第一批音乐").font(.title3.weight(.semibold))
            Text("支持 MP3、AAC、M4A、WAV、FLAC 等本地音频文件。").font(.subheadline).foregroundStyle(.secondary)
            Button("选择音频文件", systemImage: "square.and.arrow.down") { importAction() }
                .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: .infinity, minHeight: 330)
    }
}

private struct MacEmptyState: View {
    let title: String
    let systemImage: String
    let detail: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: systemImage).font(.system(size: 30)).foregroundStyle(.secondary)
            Text(title).font(.headline)
            Text(detail).font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, minHeight: 220)
        .padding(24)
    }
}
