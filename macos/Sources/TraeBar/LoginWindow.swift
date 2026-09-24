import AppKit
import WebKit

/// 内嵌 WKWebView 的登录窗口（移植自 TraeTools LoginHostForm）：
/// 加载 trae.cn 让用户正常登录，轮询 Cookie 出现 X-Cloudide-Session 后自动关闭并回调。
/// 每次登录使用独立的非持久化 dataStore，多账号可反复弹出、互不串号。
@MainActor
final class LoginWindowController: NSWindowController, NSWindowDelegate, WKNavigationDelegate {
    private var webView: WKWebView!
    private var pollTimer: Timer?
    private let onSession: (String) -> Void
    private var gotSession = false

    init(onSession: @escaping (String) -> Void) {
        self.onSession = onSession

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 480, height: 640),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false)
        window.title = "登录 Trae 账号"
        window.center()
        window.isReleasedWhenClosed = false
        super.init(window: window)
        window.delegate = self

        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        webView = WKWebView(frame: .zero, configuration: config)
        webView.navigationDelegate = self
        window.contentView = webView

        if let url = URL(string: "https://www.trae.cn/dashboard") {
            webView.load(URLRequest(url: url))
        }

        pollTimer = Timer(timeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.pollCookies() }
        }
        RunLoop.main.add(pollTimer!, forMode: .common)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func pollCookies() {
        guard !gotSession else { return }
        webView.configuration.websiteDataStore.httpCookieStore.getAllCookies { [weak self] cookies in
            guard let self, !self.gotSession else { return }
            for c in cookies where c.name == "X-Cloudide-Session" && c.value.count > 10 {
                self.gotSession = true
                self.pollTimer?.invalidate()
                let value = c.value
                self.close()
                self.onSession(value)
                return
            }
        }
    }

    func windowWillClose(_ notification: Notification) {
        pollTimer?.invalidate()
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.orderFrontRegardless()
        window?.makeKey()
    }
}
