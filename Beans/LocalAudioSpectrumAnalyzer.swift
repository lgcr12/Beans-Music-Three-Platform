import Accelerate
import AVFoundation
import AudioToolbox
import CoreAudio
import MediaToolbox

final class LocalAudioSpectrumAnalyzer {
    var onSpectrum: (([Double]) -> Void)?

    private let fftSize = 1024
    private let bucketCount = 24
    private var tap: MTAudioProcessingTap?
    private weak var attachedItem: AVPlayerItem?
    private var format = AudioStreamBasicDescription()
    private var window = [Float]()
    private var inputReal = [Float]()
    private var inputImag = [Float]()
    private var outputReal = [Float]()
    private var outputImag = [Float]()
    private var setup: vDSP_DFT_Setup?
    private var lastPublishTime: CFTimeInterval = 0

    func attach(to item: AVPlayerItem, track: AVAssetTrack) -> Bool {
        detach()
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: Unmanaged.passUnretained(self).toOpaque(),
            init: { _, clientInfo, storage in storage.pointee = clientInfo },
            finalize: nil,
            prepare: { tap, _, processingFormat in
                let analyzer = LocalAudioSpectrumAnalyzer.analyzer(for: tap)
                analyzer.prepare(format: processingFormat.pointee)
            },
            unprepare: { tap in LocalAudioSpectrumAnalyzer.analyzer(for: tap).unprepare() },
            process: { tap, frames, _, bufferList, framesOut, flagsOut in
                let status = MTAudioProcessingTapGetSourceAudio(tap, frames, bufferList, flagsOut, nil, framesOut)
                guard status == noErr, framesOut.pointee > 0 else { return }
                LocalAudioSpectrumAnalyzer.analyzer(for: tap).process(bufferList: bufferList, frames: Int(framesOut.pointee))
            }
        )
        guard let retainedTap = makeTap(callbacks: &callbacks) else { return false }
        let parameters = AVMutableAudioMixInputParameters(track: track)
        parameters.audioTapProcessor = retainedTap
        let mix = AVMutableAudioMix()
        mix.inputParameters = [parameters]
        item.audioMix = mix
        attachedItem = item
        tap = retainedTap
        return true
    }

    private func makeTap(callbacks: inout MTAudioProcessingTapCallbacks) -> MTAudioProcessingTap? {
#if compiler(>=6.2)
        var createdTap: MTAudioProcessingTap?
        guard MTAudioProcessingTapCreate(
            kCFAllocatorDefault,
            &callbacks,
            kMTAudioProcessingTapCreationFlag_PostEffects,
            &createdTap
        ) == noErr else { return nil }
        return createdTap
#else
        var createdTap: Unmanaged<MTAudioProcessingTap>?
        guard MTAudioProcessingTapCreate(
            kCFAllocatorDefault,
            &callbacks,
            kMTAudioProcessingTapCreationFlag_PostEffects,
            &createdTap
        ) == noErr else { return nil }
        return createdTap?.takeRetainedValue()
#endif
    }

    func detach() {
        attachedItem?.audioMix = nil
        attachedItem = nil
        tap = nil
    }

    private static func analyzer(for tap: MTAudioProcessingTap) -> LocalAudioSpectrumAnalyzer {
        Unmanaged<LocalAudioSpectrumAnalyzer>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
    }

    private func prepare(format: AudioStreamBasicDescription) {
        self.format = format
        unprepare()
        window = [Float](repeating: 0, count: fftSize)
        inputReal = [Float](repeating: 0, count: fftSize)
        inputImag = [Float](repeating: 0, count: fftSize)
        outputReal = [Float](repeating: 0, count: fftSize)
        outputImag = [Float](repeating: 0, count: fftSize)
        vDSP_hann_window(&window, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        setup = vDSP_DFT_zop_CreateSetup(nil, vDSP_Length(fftSize), .FORWARD)
    }

    private func unprepare() {
        if let setup { vDSP_DFT_DestroySetup(setup) }
        setup = nil
        window.removeAll(keepingCapacity: false)
        inputReal.removeAll(keepingCapacity: false)
        inputImag.removeAll(keepingCapacity: false)
        outputReal.removeAll(keepingCapacity: false)
        outputImag.removeAll(keepingCapacity: false)
    }

    private func process(bufferList: UnsafeMutablePointer<AudioBufferList>, frames: Int) {
        guard let setup,
              format.mFormatID == kAudioFormatLinearPCM,
              format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
              format.mBitsPerChannel == 32,
              !inputReal.isEmpty else { return }

        let now = CACurrentMediaTime()
        let targetFPS = ProcessInfo.processInfo.isLowPowerModeEnabled ? 20.0 : 30.0
        guard now - lastPublishTime >= 1.0 / targetFPS else { return }
        lastPublishTime = now

        let buffers = UnsafeMutableAudioBufferListPointer(bufferList)
        guard let first = buffers.first, let rawData = first.mData else { return }
        let availableFrames = min(frames, fftSize)
        let channels = max(1, Int(format.mChannelsPerFrame))
        let isInterleaved = format.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0
        let source = rawData.assumingMemoryBound(to: Float.self)
        inputReal.withUnsafeMutableBufferPointer { destination in
            destination.initialize(repeating: 0)
            if isInterleaved {
                for index in 0..<availableFrames { destination[index] = source[index * channels] }
            } else {
                destination.baseAddress?.update(from: source, count: availableFrames)
            }
        }
        vDSP_vmul(inputReal, 1, window, 1, &inputReal, 1, vDSP_Length(fftSize))
        vDSP_DFT_Execute(setup, inputReal, inputImag, &outputReal, &outputImag)

        var levels = [Double](repeating: 0, count: bucketCount)
        let usableBins = fftSize / 2
        for bucket in 0..<bucketCount {
            let start = max(1, Int(pow(Double(usableBins), Double(bucket) / Double(bucketCount))))
            let end = min(usableBins, max(start + 1, Int(pow(Double(usableBins), Double(bucket + 1) / Double(bucketCount)))))
            var peak: Float = 0
            for index in start..<end {
                peak = max(peak, hypot(outputReal[index], outputImag[index]))
            }
            levels[bucket] = min(1, max(0, log10(1 + Double(peak)) / 2.2))
        }
        DispatchQueue.main.async { [weak self] in self?.onSpectrum?(levels) }
    }
}
