import AppKit
import ApplicationServices
import ServiceManagement

private let appName = "Talk Password Fill"

private final class ScanToken: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool { lock.withLock { cancelled } }

    func cancel() { lock.withLock { cancelled = true } }
}

@MainActor
private final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = PasswordStore()
    private let autofill = Autofill()
    private let queue = DispatchQueue(label: "com.local.talk-password-fill.scan", qos: .utility)
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let statusLine = NSMenuItem(title: "Starting", action: nil, keyEquivalent: "")
    private let retryItem = NSMenuItem(title: "Fill Again", action: #selector(retry), keyEquivalent: "")
    private let pauseItem = NSMenuItem(title: "Pause Autofill", action: #selector(togglePause), keyEquivalent: "")
    private let editItem = NSMenuItem(title: "Set Password", action: #selector(editPassword), keyEquivalent: "")
    private let deleteItem = NSMenuItem(title: "Delete Saved Password", action: #selector(deletePassword), keyEquivalent: "")
    private let loginItem = NSMenuItem(title: "Launch at Mac Login", action: #selector(toggleLoginItem), keyEquivalent: "")
    private var scanToken: ScanToken?
    private var timer: Timer?
    private var retryGeneration = 0
    private var registered = false
    private var showingDialog = false
    private var paused = UserDefaults.standard.bool(forKey: "autofillPaused")

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureMenu()
        registered = store.exists()
        timer = Timer.scheduledTimer(timeInterval: 1.2, target: self, selector: #selector(poll), userInfo: nil, repeats: true)
        poll()
        if !registered {
            DispatchQueue.main.async { [weak self] in self?.editPassword() }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !registered { editPassword() }
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        scanToken?.cancel()
    }

    private func configureMenu() {
        if let button = statusItem.button {
            button.title = "Talk Fill"
            button.image = NSImage(systemSymbolName: "key.fill", accessibilityDescription: nil)
            button.imagePosition = .imageLeading
            button.setAccessibilityLabel(appName)
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(retryItem)
        menu.addItem(pauseItem)
        menu.addItem(.separator())
        menu.addItem(editItem)
        menu.addItem(deleteItem)
        menu.addItem(menuItem("Open Accessibility Settings", #selector(openAccessibilitySettings)))
        menu.addItem(loginItem)
        menu.addItem(.separator())
        menu.addItem(menuItem("Quit", #selector(quit), key: "q"))
        for item in [retryItem, pauseItem, editItem, deleteItem, loginItem] { item.target = self }
        statusItem.menu = menu
    }

    private func menuItem(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    func menuWillOpen(_ menu: NSMenu) {
        editItem.title = registered ? "Change Password" : "Set Password"
        pauseItem.title = paused ? "Resume Autofill" : "Pause Autofill"
        loginItem.state = switch SMAppService.mainApp.status {
        case .enabled: .on
        case .requiresApproval: .mixed
        default: .off
        }
        poll()
    }

    @objc func validateMenuItem(_ item: NSMenuItem) -> Bool {
        if item === retryItem { return registered && !paused && AXIsProcessTrusted() }
        if item === deleteItem { return registered }
        return !showingDialog
    }

    @objc private func poll() {
        guard !showingDialog else { return }
        guard registered else { return showStatus(.noPassword) }
        guard !paused else {
            statusLine.title = "Autofill is paused"
            return
        }
        guard AXIsProcessTrusted() else { return showStatus(.permissionNeeded) }
        guard scanToken == nil else { return }

        let app = NSWorkspace.shared.runningApplications.first {
            $0.bundleIdentifier == KakaoTarget.bundleID && $0.isFinishedLaunching && !$0.isTerminated
        }
        let target = app.map { KakaoTarget(pid: $0.processIdentifier, launchDate: $0.launchDate) }
        let token = ScanToken()
        scanToken = token
        let retry = retryGeneration
        queue.async { [autofill, weak self] in
            let status = autofill.scan(target: target, retry: retry, isCancelled: { token.isCancelled })
            DispatchQueue.main.async {
                guard let self else { return }
                self.scanToken = nil
                guard !token.isCancelled else {
                    self.poll()
                    return
                }
                if status == .noPassword { self.registered = false }
                self.showStatus(status)
            }
        }
    }

    private func showStatus(_ state: FillStatus) {
        if statusLine.title != state.rawValue { statusLine.title = state.rawValue }
    }

    @objc private func editPassword() {
        guard !showingDialog else { return }
        showingDialog = true
        scanToken?.cancel()
        defer {
            showingDialog = false
            poll()
        }

        let alert = NSAlert()
        alert.messageText = registered ? "Change Saved Password" : "Set KakaoTalk Password"
        alert.informativeText = "Save your password to this Mac’s Keychain and fill it in KakaoTalk. Click Log In in KakaoTalk when you’re ready."
        alert.icon = NSApp.applicationIconImage
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel").keyEquivalent = "\u{1b}"

        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 290, height: 24))
        field.placeholderString = "KakaoTalk password"
        field.setAccessibilityLabel("KakaoTalk password")
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        activate()
        guard alert.runModal() == .alertFirstButtonReturn else {
            field.stringValue = ""
            return
        }
        let password = field.stringValue
        field.stringValue = ""
        guard !password.isEmpty else {
            showMessage("Enter your password.")
            return
        }
        do {
            try store.save(password)
            registered = true
            retryGeneration += 1
            if !AXIsProcessTrusted() { requestAccessibility() }
        } catch {
            showMessage("Could not save your password to Keychain.", error.localizedDescription)
        }
    }

    @objc private func deletePassword() {
        guard !showingDialog else { return }
        showingDialog = true
        scanToken?.cancel()
        defer {
            showingDialog = false
            poll()
        }
        let alert = NSAlert()
        alert.messageText = "Delete the password saved on this Mac?"
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Delete").hasDestructiveAction = true
        activate()
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        do {
            try store.remove()
            registered = false
            retryGeneration += 1
        } catch {
            showMessage("Could not delete your password from Keychain.", error.localizedDescription)
        }
    }

    @objc private func retry() {
        retryGeneration += 1
        scanToken?.cancel()
        poll()
    }

    @objc private func togglePause() {
        paused.toggle()
        UserDefaults.standard.set(paused, forKey: "autofillPaused")
        scanToken?.cancel()
        poll()
    }

    private func requestAccessibility() {
        let options = ["AXTrustedCheckOptionPrompt": true]
        _ = AXIsProcessTrustedWithOptions(options as CFDictionary)
    }

    @objc private func openAccessibilitySettings() {
        if !AXIsProcessTrusted() { requestAccessibility() }
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func toggleLoginItem() {
        let service = SMAppService.mainApp
        do {
            switch service.status {
            case .enabled:
                try service.unregister()
            case .requiresApproval:
                SMAppService.openSystemSettingsLoginItems()
            default:
                try service.register()
                if service.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
            }
        } catch {
            showMessage("Could not change the launch-at-login setting.", error.localizedDescription)
        }
    }

    @objc private func quit() { NSApp.terminate(nil) }

    private func activate() {
        if #available(macOS 14.0, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    private func showMessage(_ title: String, _ detail: String = "") {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.addButton(withTitle: "OK")
        activate()
        alert.runModal()
    }
}

let application = NSApplication.shared
private let delegate = AppDelegate()
application.delegate = delegate
application.setActivationPolicy(.accessory)
application.run()
