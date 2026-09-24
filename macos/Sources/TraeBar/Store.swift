import Foundation

struct Account: Codable, Identifiable {
    var id = UUID()
    var name = ""                 // 用户自定义备注名
    var session = ""              // X-Cloudide-Session Cookie（敏感，文件权限 600）
    var deviceId = ""             // 16 位数字设备号，风控用
    var screenName = ""           // 从 GetUserInfo 拉取的昵称
    var mobileMasked = ""         // 脱敏手机号
    var creditsRemaining: Double = -1   // -1 = 未知
    var checkedInToday = false
    var lastCheckinDay = ""       // "yyyy-MM-dd"
    var lastResult = ""           // 最近一次签到/刷新结果说明
    var lastRun = Date.distantPast
    var sessionExpired = false
}

struct AppState: Codable {
    var accounts: [Account] = []
    var autoCheckin = true
}

/// 账号状态持久化：~/Library/Application Support/TraeBar/state.json（权限 600）。
/// 仅在主线程访问。
final class Store {
    var state = AppState()
    private let fileURL: URL

    init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TraeBar", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        fileURL = dir.appendingPathComponent("state.json")

        if let data = try? Data(contentsOf: fileURL),
           let s = try? JSONDecoder().decode(AppState.self, from: data) {
            state = s
        }
        for i in state.accounts.indices where state.accounts[i].deviceId.count != 16 {
            state.accounts[i].deviceId = TraeAPI.randomDeviceId()
        }
        rolloverDayIfNeeded()
    }

    func save() {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        enc.dateEncodingStrategy = .iso8601
        if let data = try? enc.encode(state) {
            try? data.write(to: fileURL, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
        }
    }

    func today() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: Date())
    }

    /// 跨天后重置「今日已签」标记。
    func rolloverDayIfNeeded() {
        let t = today()
        for i in state.accounts.indices where state.accounts[i].lastCheckinDay != t {
            state.accounts[i].checkedInToday = false
        }
    }

    func index(of id: UUID) -> Int? {
        state.accounts.firstIndex { $0.id == id }
    }
}
