import Foundation
import LocalAuthentication
import Security

struct PasswordStore {
    var service = Bundle.main.bundleIdentifier ?? "com.local.talk-password-fill.release"

    private var query: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: "KakaoTalk",
            kSecAttrSynchronizable as String: false,
        ]
    }

    private var backgroundQuery: [String: Any] {
        var lookup = query
        let context = LAContext()
        context.interactionNotAllowed = true
        lookup[kSecUseAuthenticationContext as String] = context
        return lookup
    }

    func exists() -> Bool {
        var lookup = backgroundQuery
        lookup[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        return SecItemCopyMatching(lookup as CFDictionary, &result) == errSecSuccess
    }

    func load() throws -> String? {
        var lookup = backgroundQuery
        lookup[kSecReturnData as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(lookup as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        try check(status)
        guard let data = result as? Data, let password = String(data: data, encoding: .utf8), !password.isEmpty else {
            throw Failure(status: errSecDecode)
        }
        return password
    }

    func save(_ password: String) throws {
        guard !password.isEmpty else { throw Failure(status: errSecParam) }
        let data = Data(password.utf8)
        var item = query
        item[kSecValueData as String] = data
        item[kSecAttrLabel as String] = "Talk Password Fill"
        let status = SecItemAdd(item as CFDictionary, nil)
        if status == errSecDuplicateItem {
            try check(SecItemUpdate(query as CFDictionary, [kSecValueData: data] as CFDictionary))
        } else {
            try check(status)
        }
    }

    func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        if status != errSecItemNotFound { try check(status) }
    }

    private func check(_ status: OSStatus) throws {
        if status != errSecSuccess { throw Failure(status: status) }
    }

    struct Failure: LocalizedError {
        let status: OSStatus

        var errorDescription: String? {
            SecCopyErrorMessageString(status, nil) as String? ?? "Keychain error (\(status))"
        }
    }
}
