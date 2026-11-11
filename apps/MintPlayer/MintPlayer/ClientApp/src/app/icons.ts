import { EnvironmentProviders, inject, provideAppInitializer } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { SparkIconRegistry } from '@mintplayer/ng-spark/services';
import link45deg from 'bootstrap-icons/icons/link-45deg.svg';
import musicNoteList from 'bootstrap-icons/icons/music-note-list.svg';
import pauseFill from 'bootstrap-icons/icons/pause-fill.svg';
import playFill from 'bootstrap-icons/icons/play-fill.svg';
import repeat from 'bootstrap-icons/icons/repeat.svg';
import repeat1 from 'bootstrap-icons/icons/repeat-1.svg';
import shuffle from 'bootstrap-icons/icons/shuffle.svg';
import skipEndFill from 'bootstrap-icons/icons/skip-end-fill.svg';
import skipStartFill from 'bootstrap-icons/icons/skip-start-fill.svg';
import soundwave from 'bootstrap-icons/icons/soundwave.svg';
import spotify from 'bootstrap-icons/icons/spotify.svg';
import volumeDownFill from 'bootstrap-icons/icons/volume-down-fill.svg';
import volumeMuteFill from 'bootstrap-icons/icons/volume-mute-fill.svg';
import volumeUpFill from 'bootstrap-icons/icons/volume-up-fill.svg';
import wikipedia from 'bootstrap-icons/icons/wikipedia.svg';
import youtube from 'bootstrap-icons/icons/youtube.svg';
import collection from 'bootstrap-icons/icons/collection.svg';
import disc from 'bootstrap-icons/icons/disc.svg';
import globe from 'bootstrap-icons/icons/globe.svg';
import infoCircle from 'bootstrap-icons/icons/info-circle.svg';
import musicNote from 'bootstrap-icons/icons/music-note.svg';
import musicNoteBeamed from 'bootstrap-icons/icons/music-note-beamed.svg';
import people from 'bootstrap-icons/icons/people.svg';
import personLinesFill from 'bootstrap-icons/icons/person-lines-fill.svg';
import tag from 'bootstrap-icons/icons/tag.svg';
import tags from 'bootstrap-icons/icons/tags.svg';

/**
 * The app's icons for `<spark-icon>`, beyond the ones ng-spark builds in (x-lg, trash, plus-lg...). Icons come from
 * this component, never from `bi` classes in markup. These are build-time files from bootstrap-icons (the `.svg`
 * loader is text), so trusting them is safe.
 */
const APP_ICONS: Record<string, string> = {
  // The player
  'music-note-list': musicNoteList,
  'pause-fill': pauseFill,
  'play-fill': playFill,
  'repeat': repeat,
  'repeat-1': repeat1,
  'shuffle': shuffle,
  'skip-end-fill': skipEndFill,
  'skip-start-fill': skipStartFill,
  'volume-down-fill': volumeDownFill,
  'volume-mute-fill': volumeMuteFill,
  'volume-up-fill': volumeUpFill,
  // The media links' sites
  'link-45deg': link45deg,
  'soundwave': soundwave,
  'spotify': spotify,
  'wikipedia': wikipedia,
  'youtube': youtube,
  // The menu's program units (App_Data/programUnits.json, "bi-<name>") and ng-spark's attribute descriptions
  'collection': collection,
  'disc': disc,
  'globe': globe,
  'info-circle': infoCircle,
  'music-note': musicNote,
  'music-note-beamed': musicNoteBeamed,
  'people': people,
  'person-lines-fill': personLinesFill,
  'tag': tag,
  'tags': tags,
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
