import AppKit
import Foundation

private struct IconSpec {
    let filename: String
    let pixels: Int
}

private let iconSpecs = [
    IconSpec(filename: "icon_16x16.png", pixels: 16),
    IconSpec(filename: "icon_16x16@2x.png", pixels: 32),
    IconSpec(filename: "icon_32x32.png", pixels: 32),
    IconSpec(filename: "icon_32x32@2x.png", pixels: 64),
    IconSpec(filename: "icon_128x128.png", pixels: 128),
    IconSpec(filename: "icon_128x128@2x.png", pixels: 256),
    IconSpec(filename: "icon_256x256.png", pixels: 256),
    IconSpec(filename: "icon_256x256@2x.png", pixels: 512),
    IconSpec(filename: "icon_512x512.png", pixels: 512),
    IconSpec(filename: "icon_512x512@2x.png", pixels: 1024)
]

private func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("Icon rendering failed: \(message)\n".utf8))
    exit(1)
}

guard CommandLine.arguments.count == 3 else {
    fail("usage: render-app-icons.swift <source.png> <output-directory>")
}

let sourceUrl = URL(fileURLWithPath: CommandLine.arguments[1])
let outputUrl = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
guard let sourceImage = NSImage(contentsOf: sourceUrl),
      sourceImage.size.width > 0,
      sourceImage.size.height > 0 else {
    fail("cannot read \(sourceUrl.path)")
}

do {
    try FileManager.default.createDirectory(
        at: outputUrl,
        withIntermediateDirectories: true)
} catch {
    fail("cannot create \(outputUrl.path): \(error.localizedDescription)")
}

for spec in iconSpecs {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: spec.pixels,
        pixelsHigh: spec.pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: NSColorSpaceName.deviceRGB,
        bytesPerRow: spec.pixels * 4,
        bitsPerPixel: 32),
        let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        fail("cannot allocate \(spec.filename)")
    }

    bitmap.size = NSSize(width: spec.pixels, height: spec.pixels)
    let targetRect = NSRect(x: 0, y: 0, width: spec.pixels, height: spec.pixels)
    let sourceRect = NSRect(origin: .zero, size: sourceImage.size)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = NSImageInterpolation.high
    context.shouldAntialias = true
    NSColor.clear.setFill()
    targetRect.fill()
    sourceImage.draw(
        in: targetRect,
        from: sourceRect,
        operation: .copy,
        fraction: 1.0,
        respectFlipped: false,
        hints: [.interpolation: NSImageInterpolation.high])
    context.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()

    guard let png = bitmap.representation(
        using: NSBitmapImageRep.FileType.png,
        properties: [:]) else {
        fail("cannot encode \(spec.filename)")
    }

    do {
        try png.write(
            to: outputUrl.appendingPathComponent(spec.filename),
            options: Data.WritingOptions.atomic)
    } catch {
        fail("cannot write \(spec.filename): \(error.localizedDescription)")
    }
}
