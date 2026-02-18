export interface AuthUser {
  isAuthenticated: boolean;
  userName: string;
  email: string;
  roles: string[];
}
