import Foundation

// MARK: - 更新检测（GitHub Releases API）

/// 检测 GitHub 最新 Release 并与当前版本比较，发现新版时用于弹窗提示。
/// 自动检查每次启动一次，手动检查随时可用。
struct UpdateChecker {
    static let repoPath = "lgcr12/Beans-Music-Three-Platform"
    static let releasePageURL = URL(string: "https://github.com/\(repoPath)/releases/latest")!
    private static let latestAPI = URL(string: "https://api.github.com/repos/\(repoPath)/releases/latest")!
    private static let suppressedVersionKey = "beans.updateCheck.suppressedVersion"
    static let automaticEnabledKey = "beans.updateCheck.automaticEnabled"
    private static let lastAutomaticCheckKey = "beans.updateCheck.lastAutomaticCheck"
    private static let etagKey = "beans.updateCheck.etag"
    private static let cachedReleaseKey = "beans.updateCheck.cachedRelease"
    private static let lastPromptedVersionKey = "beans.updateCheck.lastPromptedVersion"
    private static let remindLaterVersionKey = "beans.updateCheck.remindLaterVersion"
    private static let automaticInterval: TimeInterval = 24 * 60 * 60
    private static var automaticRequestInFlight = false

    /// 最新 Release 信息
    struct ReleaseInfo: Codable {
        let version: String
        let name: String
        let body: String
        let htmlURL: URL
        let publishedAt: Date?
        let assets: [String: URL]
        /// 当前 Apple 目标对应的安装包直链。
        let assetURL: URL?
    }

    enum CheckResult {
        case update(ReleaseInfo)
        case upToDate
        case failed
    }

    /// 当前 App 版本号（CFBundleShortVersionString）
    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
    }

    static var automaticEnabled: Bool {
        get {
            UserDefaults.standard.register(defaults: [automaticEnabledKey: true])
            return UserDefaults.standard.bool(forKey: automaticEnabledKey)
        }
        set { UserDefaults.standard.set(newValue, forKey: automaticEnabledKey) }
    }

    /// 自动检查：启动、回到前台或持续运行到期时调用；24 小时内不重复请求。
    static func checkIfNeeded() async -> ReleaseInfo? {
        guard automaticEnabled else { return nil }
        guard !automaticRequestInFlight else { return nil }
        let defaults = UserDefaults.standard
        if let last = defaults.object(forKey: lastAutomaticCheckKey) as? Date,
           Date().timeIntervalSince(last) < automaticInterval {
            return nil
        }
        automaticRequestInFlight = true
        defer { automaticRequestInFlight = false }
        guard let info = try? await fetchLatest() else { return nil }
        defaults.set(Date(), forKey: lastAutomaticCheckKey)
        guard isNewer(info.version, than: currentVersion) else { return nil }
        if defaults.string(forKey: suppressedVersionKey) == info.version { return nil }
        let reminded = defaults.string(forKey: lastPromptedVersionKey) == info.version
        let requestedReminder = defaults.string(forKey: remindLaterVersionKey) == info.version
        guard !reminded || requestedReminder else { return nil }
        defaults.set(info.version, forKey: lastPromptedVersionKey)
        defaults.removeObject(forKey: remindLaterVersionKey)
        return info
    }

    /// 手动检查：总是请求，返回明确结果（有新版本 / 已是最新 / 检查失败）
    static func checkNow() async -> CheckResult {
        do {
            let info = try await fetchLatest()
            return isNewer(info.version, than: currentVersion) ? .update(info) : .upToDate
        } catch {
            return .failed
        }
    }

    static func suppress(version: String) {
        UserDefaults.standard.set(version, forKey: suppressedVersionKey)
        UserDefaults.standard.removeObject(forKey: remindLaterVersionKey)
    }

    static func remindLater(version: String) {
        UserDefaults.standard.set(version, forKey: remindLaterVersionKey)
    }

    /// 拉取最新 Release（公开仓库无需 Token）
    static func fetchLatest() async throws -> ReleaseInfo {
        var request = URLRequest(url: latestAPI)
        request.setValue("Beans-Music/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        if let etag = UserDefaults.standard.string(forKey: etagKey), !etag.isEmpty {
            request.setValue(etag, forHTTPHeaderField: "If-None-Match")
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw URLError(.badServerResponse)
        }
        if http.statusCode == 304,
           let cached = UserDefaults.standard.data(forKey: cachedReleaseKey),
           let info = try? JSONDecoder().decode(ReleaseInfo.self, from: cached) {
            return info
        }
        guard http.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String,
              let html = json["html_url"] as? String,
              let url = URL(string: html) else {
            throw URLError(.cannotParseResponse)
        }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        let assets: [String: URL] = Dictionary(uniqueKeysWithValues: (json["assets"] as? [[String: Any]] ?? []).compactMap { item in
            guard let name = item["name"] as? String,
                  let rawURL = item["browser_download_url"] as? String,
                  let url = URL(string: rawURL) else { return nil }
            return (name, url)
        })
#if targetEnvironment(macCatalyst)
        let assetURL = assets.first(where: { $0.key.lowercased().contains("catalyst") && $0.key.lowercased().hasSuffix(".zip") })?.value
#else
        let assetURL = assets.first(where: { $0.key.lowercased().hasSuffix(".ipa") })?.value
#endif
        let publishedAt = (json["published_at"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }
        let info = ReleaseInfo(
            version: version,
            name: json["name"] as? String ?? tag,
            body: json["body"] as? String ?? "",
            htmlURL: url,
            publishedAt: publishedAt,
            assets: assets,
            assetURL: assetURL
        )
        if let etag = http.value(forHTTPHeaderField: "ETag") {
            UserDefaults.standard.set(etag, forKey: etagKey)
        }
        if let cached = try? JSONEncoder().encode(info) {
            UserDefaults.standard.set(cached, forKey: cachedReleaseKey)
        }
        return info
    }

    /// 三段式版本号比较：remote 大于 current 返回 true
    static func isNewer(_ remote: String, than current: String) -> Bool {
        func parts(_ v: String) -> [Int] {
            v.split(separator: ".").compactMap { Int($0) }
        }
        let r = parts(remote)
        let c = parts(current)
        let count = max(r.count, c.count)
        for i in 0..<count {
            let a = i < r.count ? r[i] : 0
            let b = i < c.count ? c[i] : 0
            if a != b { return a > b }
        }
        return false
    }

}
