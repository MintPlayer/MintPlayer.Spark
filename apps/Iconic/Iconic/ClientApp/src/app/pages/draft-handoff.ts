import type { IconicApi } from '../api/iconic-api';
import { normalizeScene } from '../editor/core/model';
import { dataUrlBytes } from '../editor/core/import';
import { clearDraft, readDraft } from '../editor/scene-saver';
import { dataUrlType } from '../editor/files';

/**
 * Moves the browser's draft into the signed-in user's account (D3): the scene goes to the server with its inline
 * files (painted bitmaps, uploaded fonts) turned into attachments. The draft is removed only once everything is
 * stored. Returns the new project's key, or null when there is no draft.
 */
export async function saveDraftToAccount(api: IconicApi): Promise<string | null> {
  const draft = readDraft();
  if (!draft) return null;
  const scene = normalizeScene(draft.scene);
  const files: { name: string; url: string }[] = [];
  for (const [layerId, layer] of Object.entries(scene.layers)) {
    for (const [width, value] of Object.entries(layer.bitmaps)) {
      if (!value.startsWith('data:')) continue;
      const name = `paint/${layerId}/${width}.png`;
      files.push({ name, url: value });
      layer.bitmaps[width] = name;
    }
  }
  for (const [id, font] of Object.entries(scene.fonts)) {
    if (!font.file.startsWith('data:')) continue;
    const ext = /font\/(\w+)/.exec(dataUrlType(font.file))?.[1] ?? 'ttf';
    const name = `fonts/${id}.${ext}`;
    files.push({ name, url: font.file });
    font.file = name;
  }
  const created = await api.create({ name: draft.name, kind: draft.kind, width: scene.width, height: scene.height, scene });
  for (const file of files) await api.putFile(created.key, file.name, dataUrlBytes(file.url), dataUrlType(file.url));
  clearDraft();
  return created.key;
}
