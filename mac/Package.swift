// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "DropDock",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "DropDock",
            path: "Sources/DropDock"
        )
    ]
)
