import AppKit
import Darwin

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let store = Store()
    private var runningIds = Set<UUID>()   // 正在请求的账号
    private var lockFD: Int32 = -1
    private var loginWindow: LoginWindowController?

    // MARK: - 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        // 开机自启会拉起第二个实例；拿不到独占锁就退出
        if !acquireSingleInstanceLock() {
            NSApp.terminate(nil)
            return
        }

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: "TraeBar")
            if button.image == nil { button.title = "⚡︎" }
        }
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        let timer = Timer(timeInterval: 600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.autoTick() }
        }
        RunLoop.main.add(timer, forMode: .common)

        updateTitle()
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            self?.autoTick(initial: true)
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if lockFD >= 0 { close(lockFD) }
    }

    private func acquireSingleInstanceLock() -> Bool {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TraeBar", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let path = dir.appendingPathComponent("singleton.lock").path
        let fd = open(path, O_CREAT | O_RDWR, 0o600)
        guard fd >= 0 else { return true }
        if flock(fd, LOCK_EX | LOCK_NB) != 0 {
            close(fd)
            return false
        }
        lockFD = fd
        return true
    }

    // MARK: - 菜单构建（每次打开前重建，数据永远新鲜）

    func menuNeedsUpdate(_ menu: NSMenu) {
        for item in menu.items { menu.removeItem(item) }
        populate(menu)
    }

    private func populate(_ menu: NSMenu) {
        store.rolloverDayIfNeeded()
        menu.autoenablesItems = false

        if store.state.accounts.isEmpty {
            let hint = NSMenuItem(title: "还没有账号 — 点「添加账号…」开始", action: nil, keyEquivalent: "")
            hint.isEnabled = false
            menu.addItem(hint)
            menu.addItem(.separator())
        }

        for (i, acc) in store.state.accounts.enumerated() {
            let line1 = NSMenuItem(title: "● \(displayName(acc, index: i))", action: nil, keyEquivalent: "")
            line1.isEnabled = false
            menu.addItem(line1)

            let credits = acc.creditsRemaining >= 0 ? String(format: "剩余 %.0f 分", acc.creditsRemaining) : "剩余 — 分"
            let status: String
            if runningIds.contains(acc.id) {
                status = "处理中…"
            } else if acc.sessionExpired {
                status = "⚠︎ 会话已过期，请更新 Cookie"
            } else if acc.checkedInToday {
                status = "今日已签 ✓\(acc.lastResult.isEmpty ? "" : " \(acc.lastResult)")"
            } else {
                status = "今日未签到"
            }
            let line2 = NSMenuItem(title: "     \(credits) · \(status)", action: nil, keyEquivalent: "")
            line2.isEnabled = false
            menu.addItem(line2)

            menu.addItem(actionItem("     立即签到", #selector(checkinOne(_:)), represented: acc.id,
                                    enabled: !acc.sessionExpired && !runningIds.contains(acc.id)))
            menu.addItem(actionItem("     更新会话 Cookie…", #selector(updateSession(_:)), represented: acc.id))
            menu.addItem(actionItem("     删除账号…", #selector(removeAccount(_:)), represented: acc.id))
            menu.addItem(.separator())
        }

        let hasAccounts = !store.state.accounts.isEmpty
        menu.addItem(actionItem("全部签到", #selector(checkinAll(_:)), enabled: hasAccounts))
        menu.addItem(actionItem("刷新状态与积分", #selector(refreshAll(_:)), enabled: hasAccounts))
        menu.addItem(actionItem("登录账号…（手机号/扫码，自动获取）", #selector(loginAccount(_:))))
        menu.addItem(actionItem("手动粘贴 Cookie 添加…", #selector(addAccount(_:))))
        menu.addItem(.separator())
        menu.addItem(toggleItem("自动每日签到（每 10 分钟检查）", on: store.state.autoCheckin,
                                action: #selector(toggleAuto(_:))))
        menu.addItem(toggleItem("开机自启", on: launchAgentInstalled(),
                                action: #selector(toggleLaunchAgent(_:))))
        menu.addItem(.separator())
        menu.addItem(actionItem("退出 TraeBar", #selector(quit(_:))))
    }

    private func actionItem(_ title: String, _ action: Selector, represented: Any? = nil, enabled: Bool = true) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        item.isEnabled = enabled
        if let rep = represented { item.representedObject = rep }
        return item
    }

    private func toggleItem(_ title: String, on: Bool, action: Selector) -> NSMenuItem {
        actionItem((on ? "✓ " : "") + title, action)
    }

    private func displayName(_ acc: Account, index: Int) -> String {
        var n = acc.name.isEmpty ? (acc.screenName.isEmpty ? "账号\(index + 1)" : acc.screenName) : acc.name
        if !acc.mobileMasked.isEmpty { n += "（\(acc.mobileMasked)）" }
        return n
    }

    private func updateTitle() {
        guard let button = statusItem?.button else { return }
        let known = store.state.accounts.map(\.creditsRemaining).filter { $0 >= 0 }
        if known.isEmpty {
            button.title = ""
            button.image = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: "TraeBar")
            if button.image == nil { button.title = "⚡︎" }
            return
        }
        let sum = known.reduce(0, +)
        let anyExpired = store.state.accounts.contains { $0.sessionExpired }
        button.image = nil
        button.title = (anyExpired ? "⚠︎ " : "⚡︎ ") + String(format: "%.0f", sum)
    }

    // MARK: - 账号增删改

    /// 弹出内嵌登录窗，登录成功自动抓取 X-Cloudide-Session（无需手动复制 Cookie）。
    @objc private func loginAccount(_ sender: Any?) {
        loginWindow = LoginWindowController { [weak self] session in
            guard let self else { return }
            self.loginWindow = nil
            guard session.count > 10 else { return }
            // 已存在同一会话的账号则去重，只刷新
            if let idx = self.store.state.accounts.firstIndex(where: { $0.session == session }) {
                self.store.state.accounts[idx].sessionExpired = false
                self.store.save()
                self.refreshAndMaybeClaim(self.store.state.accounts[idx].id, true)
                return
            }
            var acc = Account()
            acc.session = session
            acc.deviceId = TraeAPI.randomDeviceId()
            self.store.state.accounts.append(acc)
            self.store.save()
            self.updateTitle()
            self.refreshAndMaybeClaim(acc.id, true)
        }
        loginWindow?.show()
    }

    @objc private func addAccount(_ sender: Any?) {
        let (ok, name, session) = promptAccount(existing: nil)
        guard ok, session.count > 10 else {
            if ok { showOops("Cookie 看起来不对（太短），请复制完整的 X-Cloudide-Session 值。") }
            return
        }
        var acc = Account()
        acc.name = name
        acc.session = session
        acc.deviceId = TraeAPI.randomDeviceId()
        store.state.accounts.append(acc)
        store.save()
        updateTitle()
        refreshAndMaybeClaim(acc.id, true)
    }

    @objc private func updateSession(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID, let idx = store.index(of: id) else { return }
        let old = store.state.accounts[idx]
        let (ok, name, session) = promptAccount(existing: old)
        guard ok, session.count > 10 else {
            if ok { showOops("Cookie 看起来不对（太短），请复制完整的 X-Cloudide-Session 值。") }
            return
        }
        store.state.accounts[idx].session = session
        store.state.accounts[idx].sessionExpired = false
        if !name.isEmpty { store.state.accounts[idx].name = name }
        store.save()
        refreshAndMaybeClaim(id, false)
    }

    @objc private func removeAccount(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID, let idx = store.index(of: id) else { return }
        let acc = store.state.accounts[idx]
        let alert = NSAlert()
        alert.messageText = "删除账号「\(displayName(acc, index: idx))」？"
        alert.informativeText = "只从 TraeBar 移除，不影响浏览器里的登录状态。"
        alert.addButton(withTitle: "删除")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn {
            store.state.accounts.remove(at: idx)
            store.save()
            updateTitle()
        }
    }

    /// 弹窗输入备注名 + Cookie；兼容整段粘贴 "X-Cloudide-Session=xxx" 的形式。
    private func promptAccount(existing: Account?) -> (Bool, String, String) {
        let alert = NSAlert()
        alert.messageText = existing == nil ? "添加 Trae 账号" : "更新会话 Cookie"
        alert.informativeText = "浏览器登录 trae.cn → F12 → 应用(Application) → Cookies → 复制 X-Cloudide-Session 的值。\n有效期约 14 天，过期后在菜单里选「更新会话 Cookie」。"

        let nameField = NSTextField(frame: NSRect(x: 0, y: 58, width: 340, height: 24))
        nameField.placeholderString = "备注名（可空，如：工作号）"
        nameField.stringValue = existing?.name ?? ""
        let cookieField = NSSecureTextField(frame: NSRect(x: 0, y: 26, width: 340, height: 24))
        cookieField.placeholderString = "X-Cloudide-Session 的值"
        let box = NSView(frame: NSRect(x: 0, y: 0, width: 340, height: 88))
        box.addSubview(nameField)
        box.addSubview(cookieField)
        alert.accessoryView = box
        alert.addButton(withTitle: "确定")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return (false, "", "") }

        var s = cookieField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if let range = s.range(of: "X-Cloudide-Session=") {
            s = String(s[range.upperBound...]).trimmingCharacters(in: .whitespaces)
        }
        return (true, nameField.stringValue.trimmingCharacters(in: .whitespaces), s)
    }

    private func showOops(_ text: String) {
        let alert = NSAlert()
        alert.messageText = "TraeBar"
        alert.informativeText = text
        alert.addButton(withTitle: "好")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    // MARK: - 签到 / 刷新

    @objc private func checkinOne(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID else { return }
        refreshAndMaybeClaim(id, true)
    }

    @objc private func checkinAll(_ sender: Any?) {
        for (i, acc) in store.state.accounts.enumerated() where !acc.sessionExpired {
            let id = acc.id
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 2.0) { [weak self] in
                self?.refreshAndMaybeClaim(id, true)
            }
        }
    }

    @objc private func refreshAll(_ sender: Any?) {
        for (i, acc) in store.state.accounts.enumerated() where !acc.sessionExpired {
            let id = acc.id
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 1.0) { [weak self] in
                self?.refreshAndMaybeClaim(id, false)
            }
        }
    }

    private func refreshAndMaybeClaim(_ id: UUID, _ doClaim: Bool) {
        guard store.index(of: id) != nil, !runningIds.contains(id) else { return }
        runningIds.insert(id)
        updateTitle()
        Task { [weak self] in
            await self?.processAccount(id: id, doClaim: doClaim)
            self?.runningIds.remove(id)
            self?.updateTitle()
        }
    }

    /// 单账号完整流程：换 token → 拉资料 → 查签到状态 →（需要时）签到 → 刷剩余积分。
    private func processAccount(id: UUID, doClaim: Bool) async {
        guard let idx = store.index(of: id) else { return }
        var acc = store.state.accounts[idx]
        defer {
            if let i = store.index(of: id) {
                store.state.accounts[i] = acc
                store.save()
            }
        }
        do {
            let token = try await TraeAPI.fetchToken(session: acc.session)
            acc.sessionExpired = false

            if acc.screenName.isEmpty || acc.mobileMasked.isEmpty {
                if let p = try? await TraeAPI.profile(token: token, session: acc.session) {
                    acc.screenName = p.screenName
                    acc.mobileMasked = p.mobileMasked
                }
            }

            var st = try await TraeAPI.checkinStatus(token: token, deviceId: acc.deviceId)

            if doClaim && !st.checkedIn && st.httpStatus == 200 {
                var attempt = 0
                while true {
                    st = try await TraeAPI.claim(token: token, deviceId: acc.deviceId)
                    attempt += 1
                    if st.code != 9074 || attempt >= 5 { break }
                    // 9074「参与用户太多」= 设备号被风控标记，换新设备号重试
                    acc.deviceId = TraeAPI.randomDeviceId()
                    try? await Task.sleep(nanoseconds: 1_500_000_000)
                }
            }

            if st.httpStatus == 200 && (st.code == 0 || st.checkedIn) {
                let gain = st.credits + st.extraCredits
                let fmt = DateFormatter()
                fmt.dateFormat = "HH:mm"
                acc.checkedInToday = true
                acc.lastCheckinDay = store.today()
                acc.lastResult = gain > 0
                    ? String(format: "+%.0f 分 · %@", gain, fmt.string(from: Date()))
                    : "✓ \(fmt.string(from: Date()))"
            } else {
                acc.lastResult = "失败：\(st.message.isEmpty ? "HTTP \(st.httpStatus)/code \(st.code)" : st.message)"
            }

            if let remain = try? await TraeAPI.remainingCredits(token: token, deviceId: acc.deviceId) {
                acc.creditsRemaining = remain
            }
            acc.lastRun = Date()
        } catch let e as TraeError {
            if case .sessionExpired = e {
                acc.sessionExpired = true
                acc.lastResult = "会话已过期"
            } else {
                acc.lastResult = "错误：\(e.localizedDescription)"
            }
        } catch {
            acc.lastResult = "错误：\(error.localizedDescription)"
        }
    }

    // MARK: - 自动签到

    private func autoTick(initial: Bool = false) {
        guard store.state.autoCheckin || initial else { return }
        store.rolloverDayIfNeeded()

        if initial && !store.state.autoCheckin {
            // 自动签到关闭时，启动仅刷新状态
            for acc in store.state.accounts where !acc.sessionExpired {
                refreshAndMaybeClaim(acc.id, false)
            }
            return
        }
        for acc in store.state.accounts
        where !acc.sessionExpired && !acc.checkedInToday {
            refreshAndMaybeClaim(acc.id, true)
        }
        if initial {
            for acc in store.state.accounts
            where !acc.sessionExpired && acc.checkedInToday {
                refreshAndMaybeClaim(acc.id, false)
            }
        }
    }

    @objc private func toggleAuto(_ sender: Any?) {
        store.state.autoCheckin.toggle()
        store.save()
    }

    // MARK: - 开机自启（LaunchAgent）

    private func launchAgentURL() -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/com.traebar.plist")
    }

    private func launchAgentInstalled() -> Bool {
        FileManager.default.fileExists(atPath: launchAgentURL().path)
    }

    @objc private func toggleLaunchAgent(_ sender: Any?) {
        let url = launchAgentURL()
        if launchAgentInstalled() {
            _ = runLaunchctl(["bootout", "gui/\(getuid())/com.traebar"])
            try? FileManager.default.removeItem(at: url)
            showOops("已关闭开机自启。")
        } else {
            let exec = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().path
            let plist: [String: Any] = [
                "Label": "com.traebar",
                "ProgramArguments": [exec],
                "RunAtLoad": true,
                "KeepAlive": false,
                "ProcessType": "Interactive",
            ]
            do {
                let dir = url.deletingLastPathComponent()
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
                try data.write(to: url)
                showOops("已开启开机自启（登录时自动启动）。\n注意：如果之后移动了 TraeBar 程序的位置，请重新开关一次此选项。")
            } catch {
                showOops("写入 LaunchAgent 失败：\(error.localizedDescription)")
            }
        }
    }

    @discardableResult
    private func runLaunchctl(_ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        do {
            try p.run()
            p.waitUntilExit()
            return p.terminationStatus
        } catch {
            return -1
        }
    }

    @objc private func quit(_ sender: Any?) {
        NSApp.terminate(nil)
    }
}

@main
enum TraeBarApp {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
