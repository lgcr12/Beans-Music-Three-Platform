import SwiftUI

@main
struct BeansMusicMacApp: App {
    @StateObject private var library = MacLibraryStore()
    @StateObject private var player = MacPlayer()

    var body: some Scene {
        WindowGroup {
            MacRootView()
                .environmentObject(library)
                .environmentObject(player)
                .frame(minWidth: 980, minHeight: 650)
        }
        .commands {
            CommandGroup(after: .newItem) {
                Button("导入本地音乐…") {
                    NotificationCenter.default.post(name: .beansMacImportAudio, object: nil)
                }
                .keyboardShortcut("o", modifiers: [.command, .shift])
            }
            CommandMenu("播放") {
                Button("播放或暂停") {
                    NotificationCenter.default.post(name: .beansMacTogglePlayback, object: nil)
                }
                .keyboardShortcut(.space, modifiers: [])
                Button("下一首") {
                    NotificationCenter.default.post(name: .beansMacNextSong, object: nil)
                }
                .keyboardShortcut(.rightArrow, modifiers: [.command])
                Button("上一首") {
                    NotificationCenter.default.post(name: .beansMacPreviousSong, object: nil)
                }
                .keyboardShortcut(.leftArrow, modifiers: [.command])
            }
        }
    }
}

extension Notification.Name {
    static let beansMacImportAudio = Notification.Name("beans.mac.importAudio")
    static let beansMacTogglePlayback = Notification.Name("beans.mac.togglePlayback")
    static let beansMacNextSong = Notification.Name("beans.mac.nextSong")
    static let beansMacPreviousSong = Notification.Name("beans.mac.previousSong")
}
