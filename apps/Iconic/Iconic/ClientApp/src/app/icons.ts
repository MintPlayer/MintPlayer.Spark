import { EnvironmentProviders, inject, provideAppInitializer } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { SparkIconRegistry } from '@mintplayer/ng-spark/services';
import activity from 'bootstrap-icons/icons/activity.svg';
import arrowClockwise from 'bootstrap-icons/icons/arrow-clockwise.svg';
import arrowCounterclockwise from 'bootstrap-icons/icons/arrow-counterclockwise.svg';
import arrowsMove from 'bootstrap-icons/icons/arrows-move.svg';
import bezier from 'bootstrap-icons/icons/bezier.svg';
import bezier2 from 'bootstrap-icons/icons/bezier2.svg';
import brush from 'bootstrap-icons/icons/brush.svg';
import chevronDown from 'bootstrap-icons/icons/chevron-down.svg';
import chevronUp from 'bootstrap-icons/icons/chevron-up.svg';
import circle from 'bootstrap-icons/icons/circle.svg';
import circleHalf from 'bootstrap-icons/icons/circle-half.svg';
import cloudUpload from 'bootstrap-icons/icons/cloud-upload.svg';
import crosshair from 'bootstrap-icons/icons/crosshair.svg';
import cursor from 'bootstrap-icons/icons/cursor.svg';
import dot from 'bootstrap-icons/icons/dot.svg';
import download from 'bootstrap-icons/icons/download.svg';
import egg from 'bootstrap-icons/icons/egg.svg';
import eraser from 'bootstrap-icons/icons/eraser.svg';
import eye from 'bootstrap-icons/icons/eye.svg';
import eyeSlash from 'bootstrap-icons/icons/eye-slash.svg';
import eyedropper from 'bootstrap-icons/icons/eyedropper.svg';
import files from 'bootstrap-icons/icons/files.svg';
import flag from 'bootstrap-icons/icons/flag.svg';
import fonts from 'bootstrap-icons/icons/fonts.svg';
import hexagon from 'bootstrap-icons/icons/hexagon.svg';
import image from 'bootstrap-icons/icons/image.svg';
import intersect from 'bootstrap-icons/icons/intersect.svg';
import key from 'bootstrap-icons/icons/key.svg';
import link45deg from 'bootstrap-icons/icons/link-45deg.svg';
import lock from 'bootstrap-icons/icons/lock.svg';
import paintBucket from 'bootstrap-icons/icons/paint-bucket.svg';
import pauseFill from 'bootstrap-icons/icons/pause-fill.svg';
import pentagon from 'bootstrap-icons/icons/pentagon.svg';
import pinAngle from 'bootstrap-icons/icons/pin-angle.svg';
import playFill from 'bootstrap-icons/icons/play-fill.svg';
import scissors from 'bootstrap-icons/icons/scissors.svg';
import slashLg from 'bootstrap-icons/icons/slash-lg.svg';
import sliders from 'bootstrap-icons/icons/sliders.svg';
import square from 'bootstrap-icons/icons/square.svg';
import star from 'bootstrap-icons/icons/star.svg';
import unlock from 'bootstrap-icons/icons/unlock.svg';
import upload from 'bootstrap-icons/icons/upload.svg';
import zoomIn from 'bootstrap-icons/icons/zoom-in.svg';
import zoomOut from 'bootstrap-icons/icons/zoom-out.svg';

/**
 * The app's icons for `<spark-icon>`, beyond ng-spark's built-ins (x-lg, trash, plus-lg, pencil, share...). Icons come
 * from this component, never from `bi` classes in markup. Build-time files from bootstrap-icons (the `.svg` loader is
 * text), so trusting them is safe.
 */
const APP_ICONS: Record<string, string> = {
  // The tool palette (editor/toolbar/tool-palette.component.ts)
  'cursor': cursor,
  'arrows-move': arrowsMove,
  'dot': dot,
  'slash-lg': slashLg,
  'activity': activity,
  'bezier2': bezier2,
  'circle-half': circleHalf,
  'square': square,
  'circle': circle,
  'egg': egg,
  'pentagon': pentagon,
  'bezier': bezier,
  'hexagon': hexagon,
  'star': star,
  'fonts': fonts,
  'eraser': eraser,
  'paint-bucket': paintBucket,
  'eyedropper': eyedropper,
  'crosshair': crosshair,
  // The canvas and the panels
  'zoom-in': zoomIn,
  'zoom-out': zoomOut,
  'arrow-counterclockwise': arrowCounterclockwise,
  'arrow-clockwise': arrowClockwise,
  'brush': brush,
  'chevron-up': chevronUp,
  'chevron-down': chevronDown,
  'eye': eye,
  'eye-slash': eyeSlash,
  'lock': lock,
  'unlock': unlock,
  'pin-angle': pinAngle,
  'scissors': scissors,
  'intersect': intersect,
  'sliders': sliders,
  'key': key,
  'pause-fill': pauseFill,
  'play-fill': playFill,
  // Files and sharing
  'upload': upload,
  'download': download,
  'cloud-upload': cloudUpload,
  'files': files,
  'flag': flag,
  'link-45deg': link45deg,
  'image': image,
};

export function provideAppIcons(): EnvironmentProviders {
  return provideAppInitializer(() => {
    const registry = inject(SparkIconRegistry);
    const sanitizer = inject(DomSanitizer);
    for (const [name, svg] of Object.entries(APP_ICONS)) {
      registry.register(name, sanitizer.bypassSecurityTrustHtml(svg));
    }
  });
}
