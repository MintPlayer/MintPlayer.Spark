// Models
export type {
  AuthUser,
} from './lib/models/auth-user';
export type {
  SparkAuthConfig,
} from './lib/models/auth-config';
export {
  SPARK_AUTH_CONFIG,
  defaultSparkAuthConfig,
} from './lib/models/auth-config';
export type {
  SparkAuthRouteEntry,
  SparkAuthRouteConfig,
  SparkAuthRoutePaths,
} from './lib/models/auth-route-config';
export {
  SPARK_AUTH_ROUTE_PATHS,
} from './lib/models/auth-route-config';

// Services
export { SparkAuthService } from './lib/services/spark-auth.service';
export { SparkAuthTranslationService } from './lib/services/spark-auth-translation.service';

// Pipes
export { TranslateKeyPipe } from './lib/pipes/translate-key.pipe';

// Interceptors
export { sparkAuthInterceptor } from './lib/interceptors/spark-auth.interceptor';

// Guards
export { sparkAuthGuard } from './lib/guards/spark-auth.guard';

// Providers
export { provideSparkAuth, withSparkAuth } from './lib/providers/provide-spark-auth';

// Routes
export { sparkAuthRoutes } from './lib/routes/spark-auth-routes';

// Components
export { SparkAuthBarComponent } from './lib/components/auth-bar/spark-auth-bar.component';
export { SparkLoginComponent } from './lib/components/login/spark-login.component';
export { SparkTwoFactorComponent } from './lib/components/two-factor/spark-two-factor.component';
export { SparkRegisterComponent } from './lib/components/register/spark-register.component';
export { SparkForgotPasswordComponent } from './lib/components/forgot-password/spark-forgot-password.component';
export { SparkResetPasswordComponent } from './lib/components/reset-password/spark-reset-password.component';
