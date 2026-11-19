import ApplicationServices
import Foundation
import Security

struct KakaoTarget: Equatable, Sendable {
    static let bundleID = "com.kakao.KakaoTalkMac"
    let pid: pid_t
    let launchDate: Date?

    func verifySignature() -> Bool {
        let rule = """
            anchor apple generic and identifier "com.kakao.KakaoTalkMac" and (
              certificate leaf[field.1.2.840.113635.100.6.1.9] exists or (
                certificate 1[field.1.2.840.113635.100.6.2.6] exists and
                certificate leaf[field.1.2.840.113635.100.6.1.13] exists and
                certificate leaf[subject.OU] = "L75WVXX68A"
              )
            )
            """
        var requirement: SecRequirement?
        var guest: SecCode?
        var staticCode: SecStaticCode?
        var signingInfo: CFDictionary?
        let attributes = [kSecGuestAttributePid: NSNumber(value: pid)] as CFDictionary
        guard SecRequirementCreateWithString(rule as CFString, [], &requirement) == errSecSuccess,
              let requirement,
              SecCodeCopyGuestWithAttributes(nil, attributes, [], &guest) == errSecSuccess,
              let guest,
              SecCodeCheckValidity(guest, [], requirement) == errSecSuccess,
              SecCodeCopyStaticCode(guest, [], &staticCode) == errSecSuccess,
              let staticCode,
              SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &signingInfo) == errSecSuccess,
              let info = signingInfo as? [String: Any] else { return false }
        return info[kSecCodeInfoTeamIdentifier as String] as? String == "L75WVXX68A"
    }
}

enum FillStatus: String, Sendable {
    case waitingForApp = "Waiting for KakaoTalk"
    case waitingForLogin = "Waiting for the login window"
    case filled = "Password filled"
    case hasInput = "The password field already contains text"
    case alreadyFilled = "Already filled this login window"
    case noPassword = "Set your password to start"
    case untrustedApp = "Could not verify KakaoTalk’s signature"
    case permissionNeeded = "Accessibility permission is required"
    case keychainUnavailable = "Could not access Keychain"
    case fieldUnavailable = "KakaoTalk’s password field is unavailable"
}

// Each instance is used by one serial queue.
final class Autofill: @unchecked Sendable {
    private let store = PasswordStore()
    private var verifiedTarget: KakaoTarget?
    private var filledField: AXUIElement?
    private var retryGeneration = 0

    static func loginLabel(_ label: String) -> Bool {
        ["로그인", "login", "log in", "sign in"].contains(label.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())
    }

    func scan(target: KakaoTarget?, retry: Int, isCancelled: () -> Bool) -> FillStatus {
        guard AXIsProcessTrusted() else { return .permissionNeeded }
        guard let target else {
            verifiedTarget = nil
            filledField = nil
            return .waitingForApp
        }
        if verifiedTarget != target {
            verifiedTarget = nil
            filledField = nil
            guard target.verifySignature() else { return .untrustedApp }
            verifiedTarget = target
        }
        if retryGeneration != retry {
            filledField = nil
            retryGeneration = retry
        }

        let app = AXUIElementCreateApplication(target.pid)
        AXUIElementSetMessagingTimeout(app, 0.35)
        let windows = attribute(app, kAXWindowsAttribute)
        guard let elements = windows.value as? [AXUIElement] else {
            if windows.error == .noValue {
                filledField = nil
                return .waitingForLogin
            }
            return .fieldUnavailable
        }
        let loginWindows = elements.filter {
            guard let title = string($0, kAXTitleAttribute) else { return false }
            return Self.loginLabel(title)
        }
        if loginWindows.isEmpty {
            filledField = nil
            return .waitingForLogin
        }
        guard loginWindows.count == 1, let window = loginWindows.first,
              let field = passwordField(in: window) else { return .fieldUnavailable }

        var owner: pid_t = 0
        var settable = DarwinBoolean(false)
        guard AXUIElementGetPid(field, &owner) == .success, owner == target.pid,
              attribute(field, kAXEnabledAttribute).value as? Bool != false,
              AXUIElementIsAttributeSettable(field, kAXValueAttribute as CFString, &settable) == .success,
              settable.boolValue else { return .fieldUnavailable }
        if let previous = filledField, CFEqual(previous, field) { return .alreadyFilled }
        guard let empty = isEmpty(field) else { return .fieldUnavailable }
        guard empty else { return .hasInput }

        let password: String
        do {
            guard let stored = try store.load() else { return .noPassword }
            password = stored
        } catch {
            return .keychainUnavailable
        }

        guard isEmpty(field) == true, !isCancelled() else { return .fieldUnavailable }
        let result = AXUIElementSetAttributeValue(field, kAXValueAttribute as CFString, password as CFString)
        guard result == .success else { return .fieldUnavailable }
        filledField = field
        return .filled
    }

    private func passwordField(in window: AXUIElement) -> AXUIElement? {
        var pending = [window]
        var visited = Set<AXUIElement>()
        var secureFields: [AXUIElement] = []
        var hasLoginButton = false
        while let element = pending.popLast() {
            guard visited.insert(element).inserted else { continue }
            guard visited.count <= 160 else { return nil }
            if string(element, kAXSubroleAttribute) == kAXSecureTextFieldSubrole {
                secureFields.append(element)
            }
            if string(element, kAXRoleAttribute) == kAXButtonRole,
               [kAXTitleAttribute, kAXDescriptionAttribute].contains(where: { name in
                   string(element, name).map(Self.loginLabel) == true
               }) {
                hasLoginButton = true
            }
            if let children = attribute(element, kAXChildrenAttribute).value as? [AXUIElement] {
                pending.append(contentsOf: children)
            }
        }
        guard secureFields.count == 1, let field = secureFields.first else { return nil }
        let placeholder = string(field, kAXPlaceholderValueAttribute)?.lowercased()
        guard hasLoginButton || placeholder == "비밀번호" || placeholder == "password" else { return nil }
        return field
    }

    private func isEmpty(_ field: AXUIElement) -> Bool? {
        let result = attribute(field, kAXValueAttribute)
        if let text = result.value as? String { return text.isEmpty }
        if let length = attribute(field, kAXNumberOfCharactersAttribute).value as? Int { return length == 0 }
        return result.error == .noValue ? true : nil
    }

    private func string(_ element: AXUIElement, _ name: String) -> String? {
        attribute(element, name).value as? String
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> (error: AXError, value: CFTypeRef?) {
        var value: CFTypeRef?
        let error = AXUIElementCopyAttributeValue(element, name as CFString, &value)
        return (error, value)
    }
}
