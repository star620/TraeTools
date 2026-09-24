import Foundation

enum TraeError: Error, LocalizedError {
    case sessionExpired
    case badResponse(String)

    var errorDescription: String? {
        switch self {
        case .sessionExpired:
            return "会话已过期（约 14 天有效期），请重新复制 Cookie"
        case .badResponse(let s):
            return "响应异常：\(s)"
        }
    }
}

/// Trae 云端 API 客户端。
/// 接口与参数逆向自 TraeTools（Services/Checkin/TraeApiClient.cs、Services/Usage/TraeUsageClient.cs）。
enum TraeAPI {
    static let base = URL(string: "https://api.trae.cn")!

    struct Profile {
        var userId = ""
        var screenName = ""
        var mobileMasked = ""
    }

    struct CheckinResult {
        var httpStatus = 0
        var code = -1
        var message = ""
        var checkedIn = false
        var credits: Double = 0
        var extraCredits: Double = 0
    }

    // MARK: - 公开接口

    /// 用 X-Cloudide-Session Cookie 换取 8 小时有效的 JWT。
    static func fetchToken(session: String) async throws -> String {
        let (status, obj) = try await post(
            "/cloudide/api/v3/common/GetUserToken",
            headers: [
                "Cookie": "X-Cloudide-Session=" + session,
                "Referer": "https://www.trae.cn/",
                "Origin": "https://www.trae.cn",
            ])
        if status == 401 { throw TraeError.sessionExpired }
        let token = ((obj["Result"] as? [String: Any])?["Token"] as? String) ?? ""
        if status != 200 || token.isEmpty {
            throw TraeError.badResponse("GetUserToken HTTP \(status)")
        }
        return token
    }

    /// 查询今日签到状态与单日奖励。
    static func checkinStatus(token: String, deviceId: String) async throws -> CheckinResult {
        let (s, o) = try await post("/trae/api/v2/ug/checkin_credits/status", headers: authHeaders(token: token, deviceId: deviceId))
        return parseCheckin(s, o)
    }

    /// 执行每日签到。
    static func claim(token: String, deviceId: String) async throws -> CheckinResult {
        let (s, o) = try await post("/trae/api/v2/ug/checkin_credits/claim", headers: authHeaders(token: token, deviceId: deviceId))
        return parseCheckin(s, o)
    }

    /// 汇总所有资格包的剩余积分。
    static func remainingCredits(token: String, deviceId: String) async throws -> Double {
        let (_, obj) = try await post(
            "/trae/api/v2/pay/user_current_entitlement_list",
            headers: authHeaders(token: token, deviceId: deviceId))
        guard let packs = obj["user_entitlement_pack_list"] as? [[String: Any]] else {
            throw TraeError.badResponse("响应缺少 user_entitlement_pack_list")
        }
        if packs.isEmpty {
            throw TraeError.badResponse("积分包列表为空（可能未登录/风控）")
        }
        var total = 0.0
        for p in packs {
            let limit = doubleValue(((p["entitlement_base_info"] as? [String: Any])?["quota"] as? [String: Any])?["credits_limit"])
            let used = doubleValue((p["usage"] as? [String: Any])?["credits_amount"])
            total += max(0, limit - used)
        }
        return total
    }

    /// 拉取账号资料（昵称/脱敏手机号）。
    static func profile(token: String, session: String) async throws -> Profile? {
        let (status, obj) = try await post(
            "/cloudide/api/v3/trae/GetUserInfo",
            headers: [
                "Authorization": "Cloud-IDE-JWT " + token,
                "Cookie": "X-Cloudide-Session=" + session,
                "Referer": "https://www.trae.cn/",
                "Origin": "https://www.trae.cn",
            ])
        if status == 401 { throw TraeError.sessionExpired }
        guard let r = obj["Result"] as? [String: Any] else { return nil }
        var p = Profile()
        p.userId = (r["UserID"] as? String) ?? ""
        p.screenName = (r["ScreenName"] as? String) ?? ""
        p.mobileMasked = (r["NonPlainTextMobile"] as? String) ?? ""
        return p
    }

    /// 风控要求：设备号必须是 16 位纯数字（仿 Aha 设备号），UUID 会触发 9074「参与用户太多」。
    static func randomDeviceId() -> String {
        String(UInt64.random(in: 1_000_000_000_000_000...9_999_999_999_999_999))
    }

    // MARK: - 内部

    private static func authHeaders(token: String, deviceId: String) -> [String: String] {
        [
            "Authorization": "Cloud-IDE-JWT " + token,
            "X-User-Region": "cn",
            "x-device-id": deviceId,
            "Content-Type": "application/json",
        ]
    }

    @discardableResult
    private static func post(_ path: String, headers: [String: String], body: String = "{}") async throws -> (Int, [String: Any]) {
        var req = URLRequest(url: URL(string: path, relativeTo: base)!)
        req.httpMethod = "POST"
        req.timeoutInterval = 30
        req.httpBody = body.data(using: .utf8)
        for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        req.setValue("TraeBar/1.0", forHTTPHeaderField: "User-Agent")

        let data: Data
        let resp: URLResponse
        do {
            (data, resp) = try await URLSession.shared.data(for: req)
        } catch {
            throw TraeError.badResponse("网络错误：\(error.localizedDescription)")
        }
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        var obj: [String: Any] = [:]
        if !data.isEmpty, let parsed = try? JSONSerialization.jsonObject(with: data),
           let dict = parsed as? [String: Any] {
            obj = dict
        }
        return (status, obj)
    }

    private static func parseCheckin(_ status: Int, _ obj: [String: Any]) -> CheckinResult {
        var r = CheckinResult()
        r.httpStatus = status
        if let c = obj["code"] as? Int {
            r.code = c
        } else if let c = obj["code"] as? String, let v = Int(c) {
            r.code = v
        }
        r.message = (obj["message"] as? String) ?? ""
        r.checkedIn = (obj["checked_in"] as? Bool) ?? false
        r.credits = doubleValue(obj["credits"])
        r.extraCredits = doubleValue(obj["extra_credits"])
        return r
    }

    /// 接口的数字字段可能以字符串返回，兼容两种。
    private static func doubleValue(_ any: Any?) -> Double {
        if let d = any as? Double { return d }
        if let i = any as? Int { return Double(i) }
        if let n = any as? NSNumber { return n.doubleValue }
        if let s = any as? String { return Double(s) ?? 0 }
        return 0
    }
}
