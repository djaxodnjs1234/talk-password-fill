import AppKit
import ApplicationServices

@main
@MainActor
final class FocusedPasswordEdit: NSObject, NSApplicationDelegate, NSTextFieldDelegate {
    private let testPassword = "Probe-확인!"
    private let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 152),
                                  styleMask: [.titled], backing: .buffered, defer: false)
    private let account = NSTextField(frame: NSRect(x: 20, y: 94, width: 280, height: 24))
    private let field = NSSecureTextField(frame: NSRect(x: 20, y: 58, width: 280, height: 24))
    private let login = NSButton(frame: NSRect(x: 20, y: 20, width: 90, height: 24))
    private var editCount = 0
    private var ranTest = false

    static func main() {
        let fixture = FocusedPasswordEdit()
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        app.delegate = fixture
        app.run()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        window.title = "Test Login"
        account.stringValue = "Fixture account"
        field.delegate = self
        login.title = "Log In"
        login.isEnabled = false
        window.contentView?.addSubview(account)
        window.contentView?.addSubview(field)
        window.contentView?.addSubview(login)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(account)
        NSApp.activate(ignoringOtherApps: true)
        scheduleVerification()
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) {
            self.fail("Bring the Test Login window forward to run the fixture.")
        }
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        scheduleVerification()
    }

    private func scheduleVerification() {
        DispatchQueue.main.async {
            guard NSApp.isActive, self.window.isVisible, !self.ranTest else { return }
            self.ranTest = true
            self.window.makeKey()
            self.window.makeFirstResponder(self.account)
            self.verify()
        }
    }

    func controlTextDidChange(_ notification: Notification) {
        editCount += 1
        login.isEnabled = !field.stringValue.isEmpty
    }

    private func verify() {
        let app = AXUIElementCreateApplication(getpid())
        guard let loginWindow = (attribute(app, kAXWindowsAttribute) as? [AXUIElement])?.first,
              let children = attribute(loginWindow, kAXChildrenAttribute) as? [AXUIElement],
              let target = children.first(where: { attribute($0, kAXSubroleAttribute) as? String == kAXSecureTextFieldSubrole })
        else { fail("The secure input fixture is unavailable.") }

        let autofill = Autofill()
        guard !login.isEnabled, editCount == 0,
              attribute(target, kAXFocusedAttribute) as? Bool == false else {
            fail("The login fixture should start with its button disabled.")
        }
        let cancelled = autofill.fill(testPassword, in: target, window: loginWindow, app: app, isCancelled: { true })
        guard cancelled != .filled, field.stringValue.isEmpty else { fail("A cancelled fill edited the field.") }

        let outcome = autofill.fill(testPassword, in: target, window: loginWindow, app: app, isCancelled: { false })
        guard outcome == .filled, field.stringValue == testPassword, login.isEnabled, editCount == 1 else {
            let frontmost = attribute(app, kAXFrontmostAttribute) as? Bool
            let hasFocusedWindow = attribute(app, kAXFocusedWindowAttribute).map { CFEqual($0, loginWindow) } ?? false
            let hasFocusedField = attribute(app, kAXFocusedUIElementAttribute).map { CFEqual($0, target) } ?? false
            print("Fixture frontmost:", frontmost as Any, "window focused:", hasFocusedWindow, "field focused:", hasFocusedField)
            fail("The password edit did not activate the fixture’s login button (\(outcome.rawValue), edits: \(editCount)).")
        }
        let second = autofill.fill("Different dummy", in: target, window: loginWindow, app: app, isCancelled: { false })
        guard second != .filled, field.stringValue == testPassword, account.stringValue == "Fixture account" else {
            fail("Autofill changed existing text or the account field.")
        }
        print("Focused secure input activated Log In. Cancellation and existing text handling passed.")
        NSApp.terminate(nil)
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        AXUIElementCopyAttributeValue(element, name as CFString, &value)
        return value
    }

    private func fail(_ message: String) -> Never {
        FileHandle.standardError.write(Data((message + "\n").utf8))
        exit(1)
    }
}
