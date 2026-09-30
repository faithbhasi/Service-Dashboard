export interface RoleRef { id: string; name: string; source: string }
export interface Me {
  id: string; displayName: string; email: string; initials: string; hasAccess: boolean;
  roles: RoleRef[]; roleName: string; permissions: string[];
  preferences: { theme: 'light' | 'dark' | 'system'; navCollapsed: boolean };
  supportContact: string; csrfToken: string;
}
export interface ModuleStatus { id: string; name: string; description: string; status: 'Active' | 'Disabled' | 'Coming Soon'; enabled: boolean }
export interface BannerInfo { type: 'Information' | 'Warning' | 'Maintenance'; text: string; dismissible: boolean }
export interface ActionPolicy { justificationRequired: boolean; justificationMinLength: number; ticketRequired: boolean; ticketPattern: string | null; typedConfirmationRequired: boolean }
export interface ActionPolicies { actions: Record<string, ActionPolicy>; mustChangePasswordDefault: boolean; generatedPasswordLength: number }
export interface Shell {
  actionPolicies: ActionPolicies;
  productName: string; environmentLabel: string; environmentLabelColor?: string; timeZone: string; dateFormat: string; supportContact: string;
  idleTimeoutMinutes: number; banner: BannerInfo | null; modules: ModuleStatus[];
}
export interface ThemeColors {
  primary: string; topBarBackground: string; navBackground: string; navText: string; navSelected: string;
  pageBackground: string; cardBackground: string; sectionHeader: string; success: string; warning: string; error: string;
}
export interface Branding {
  productName: string; light: ThemeColors; dark: ThemeColors;
  hasLogoLight?: boolean; hasLogoDark?: boolean; hasFavicon?: boolean; assetVersion?: string;
}
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; totalIsCapped: boolean }
