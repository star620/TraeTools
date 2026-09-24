// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "TraeBar",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "TraeBar",
            path: "Sources/TraeBar",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
