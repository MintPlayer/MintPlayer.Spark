// Camera scanning (warehouse PRD WX5, commerce PRD §4.11 "Scanner PWA"), on the standard Shape Detection API
// (`BarcodeDetector`, K11). The native API exists only in Chrome on Android, macOS and ChromeOS, so a host supplies a
// same-shape ponyfill (for instance the `barcode-detector` package's WebAssembly build, self-hosted and lazy-loaded)
// through `setSparkBarcodeDetectorLoader`; the native one wins when present, and the ponyfill can then be dropped.

/** The part of the standard `BarcodeDetector` this module uses. */
export interface SparkBarcodeDetector {
  detect(source: ImageBitmapSource): Promise<Array<{ rawValue: string; format: string }>>;
}

/** Creates a detector for `formats` (e.g. `['qr_code']`). */
export type SparkBarcodeDetectorFactory = (formats: string[]) => SparkBarcodeDetector | Promise<SparkBarcodeDetector>;

let loader: (() => Promise<SparkBarcodeDetectorFactory>) | null = null;

/** Registers the ponyfill used where the browser has no native `BarcodeDetector`. */
export function setSparkBarcodeDetectorLoader(load: (() => Promise<SparkBarcodeDetectorFactory>) | null): void {
  loader = load;
}

/** Whether camera scanning can work here: a camera API and a detector (native or registered). */
export function sparkCameraScanningAvailable(): boolean {
  const hasCamera = typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia;
  return hasCamera && (typeof (globalThis as { BarcodeDetector?: unknown }).BarcodeDetector === 'function' || loader !== null);
}

/** The native detector when the browser has one, else the registered ponyfill; `null` when neither exists. */
export async function createSparkBarcodeDetector(formats: string[]): Promise<SparkBarcodeDetector | null> {
  const native = (globalThis as { BarcodeDetector?: new (options: { formats: string[] }) => SparkBarcodeDetector }).BarcodeDetector;
  if (typeof native === 'function') return new native({ formats });
  if (!loader) return null;
  const factory = await loader();
  return factory(formats);
}

/** Options of {@link SparkBarcodeCamera}. */
export interface SparkBarcodeCameraOptions {
  formats?: string[];
  /** The same value is reported once per this many milliseconds (a QR code held in front of the camera). Default 2,000. */
  repeatAfterMs?: number;
  /** Milliseconds between frames. Default 150. */
  intervalMs?: number;
  /** Keep the screen on while scanning (Screen Wake Lock). Default true. */
  wakeLock?: boolean;
}

/**
 * Streams the rear camera into `video` and reports every barcode it reads. `start` asks for the camera (the
 * permission prompt); `stop` releases it and the wake lock.
 */
export class SparkBarcodeCamera {
  private stream: MediaStream | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private wake: { release(): Promise<void> } | null = null;
  private last: { value: string; at: number } | null = null;

  constructor(private readonly onScan: (value: string, format: string) => void) {}

  get running(): boolean {
    return this.stream !== null;
  }

  async start(video: HTMLVideoElement, options: SparkBarcodeCameraOptions = {}): Promise<boolean> {
    if (this.running) return true;
    const detector = await createSparkBarcodeDetector(options.formats ?? ['qr_code']);
    if (!detector || !navigator.mediaDevices?.getUserMedia) return false;
    this.stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
    video.srcObject = this.stream;
    video.setAttribute('playsinline', '');
    await video.play();
    if (options.wakeLock !== false) {
      const wakeLock = (navigator as { wakeLock?: { request(type: 'screen'): Promise<{ release(): Promise<void> }> } }).wakeLock;
      this.wake = (await wakeLock?.request('screen').catch(() => null)) ?? null;
    }
    const repeat = options.repeatAfterMs ?? 2_000;
    let busy = false;
    this.timer = setInterval(async () => {
      if (busy || video.readyState < 2) return;
      busy = true;
      try {
        for (const code of await detector.detect(video)) {
          const now = Date.now();
          if (this.last && this.last.value === code.rawValue && now - this.last.at < repeat) continue;
          this.last = { value: code.rawValue, at: now };
          this.onScan(code.rawValue, code.format);
        }
      } catch {
        // A frame the detector could not read: try the next one.
      } finally {
        busy = false;
      }
    }, options.intervalMs ?? 150);
    return true;
  }

  async stop(): Promise<void> {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.stream?.getTracks().forEach(t => t.stop());
    this.stream = null;
    await this.wake?.release().catch(() => undefined);
    this.wake = null;
  }
}
