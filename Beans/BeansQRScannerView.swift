import AVFoundation
import SwiftUI
import UIKit

struct BeansQRScannerView: UIViewControllerRepresentable {
    @Binding var scannedPayload: String

    func makeCoordinator() -> Coordinator { Coordinator(scannedPayload: $scannedPayload) }

    func makeUIViewController(context: Context) -> ScannerController {
        ScannerController(delegate: context.coordinator)
    }

    func updateUIViewController(_ uiViewController: ScannerController, context: Context) {}

    final class Coordinator: NSObject, AVCaptureMetadataOutputObjectsDelegate {
        @Binding private var scannedPayload: String
        private var delivered = false

        init(scannedPayload: Binding<String>) { _scannedPayload = scannedPayload }

        func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
            guard !delivered,
                  let code = metadataObjects.compactMap({ ($0 as? AVMetadataMachineReadableCodeObject)?.stringValue }).first,
                  code.hasPrefix("beans://") else { return }
            delivered = true
            scannedPayload = code
        }
    }
}

final class ScannerController: UIViewController {
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "com.beans.qr-camera")
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private let delegate: AVCaptureMetadataOutputObjectsDelegate

    init(delegate: AVCaptureMetadataOutputObjectsDelegate) {
        self.delegate = delegate
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .secondarySystemBackground
        guard let camera = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: camera), session.canAddInput(input) else {
            showUnavailableMessage()
            return
        }
        session.addInput(input)
        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else { showUnavailableMessage(); return }
        session.addOutput(output)
        output.setMetadataObjectsDelegate(delegate, queue: .main)
        output.metadataObjectTypes = [.qr]
        let preview = AVCaptureVideoPreviewLayer(session: session)
        preview.videoGravity = .resizeAspectFill
        view.layer.addSublayer(preview)
        previewLayer = preview
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        queue.async { [weak self] in self?.session.startRunning() }
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        queue.async { [weak self] in self?.session.stopRunning() }
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
    }

    private func showUnavailableMessage() {
        let label = UILabel()
        label.text = "当前设备没有可用摄像头\n可在下方粘贴二维码内容"
        label.textColor = .secondaryLabel
        label.textAlignment = .center
        label.numberOfLines = 0
        label.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            label.leadingAnchor.constraint(greaterThanOrEqualTo: view.leadingAnchor, constant: 20),
            label.trailingAnchor.constraint(lessThanOrEqualTo: view.trailingAnchor, constant: -20)
        ])
    }
}
