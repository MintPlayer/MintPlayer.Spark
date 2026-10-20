import { Routes } from '@angular/router';
import { SparkWaitingRoomComponent } from './spark-waiting-room.component';

/**
 * The waiting room page (commerce #521 M16), to mount under a path of the app's choosing; replaceable per route (D27).
 * Needs `withComponentInputBinding()` for the `:key` input. `withWaitingRoomRoutes()` of the storefront (M17) wraps it.
 *
 * ```ts
 * provideRouter([{ path: 'queue', children: sparkWaitingRoomRoutes() }], withComponentInputBinding())
 * ```
 * - `:key` — the room `WaitingRooms/{key}`.
 */
export function sparkWaitingRoomRoutes(): Routes {
  return [{ path: ':key', component: SparkWaitingRoomComponent }];
}
