import type { Paged } from './types';

export type Kind = 'User' | 'Computer' | 'Group';
export interface ObjectRef { id: string; name: string; kind: Kind }
export interface UacFlag { name: string; meaning: string }

export interface AdUser {
  id: string; dn: string; ou: string; samAccountName: string; userPrincipalName: string | null; displayName: string | null;
  givenName: string | null; surname: string | null; email: string | null; employeeId: string | null; title: string | null;
  department: string | null; office: string | null; phone: string | null; mobile: string | null; description: string | null;
  manager: ObjectRef | null; enabled: boolean; lockedOut: boolean;
  accountExpiry: 'Never' | 'Expires' | 'Expired'; accountExpiresUtc: string | null;
  passwordStatus: 'Expires' | 'Expired' | 'NeverExpires' | 'MustChange' | 'Unknown'; passwordExpiresUtc: string | null;
  passwordLastSetUtc: string | null; lastLogonUtc: string | null; createdUtc: string | null; changedUtc: string | null;
  resultantPso: string | null; userAccountControl: number; uacFlags: UacFlag[]; primaryGroupId: number; adminCount: boolean;
}
export interface UserDetail { user: AdUser; ouManageable: boolean; ouReason: string | null }

export interface AdComputer {
  id: string; dn: string; ou: string; name: string; dnsHostName: string | null; enabled: boolean; operatingSystem: string | null;
  osVersion: string | null; lastLogonUtc: string | null; passwordLastSetUtc: string | null; managedBy: ObjectRef | null;
  description: string | null; changedUtc: string | null; createdUtc: string | null; lastLoggedInUser: string | null;
}
export interface ComputerDetail { computer: AdComputer; ouManageable: boolean; ouReason: string | null }

export interface AdGroup {
  id: string; dn: string; ou: string; name: string; description: string | null; scope: string; type: string;
  managedBy: ObjectRef | null; memberCount: number | null; isProtected: boolean; isManageable: boolean; blockReason: string | null;
}
export interface Memberships { direct: AdGroup[]; nested: { group: AdGroup; via: string | null }[]; primary: AdGroup | null }
export interface GroupMember {
  id: string; kind: 'User' | 'Computer' | 'Group'; name: string; samAccountName: string | null; email: string | null; dn: string; enabled: boolean | null;
}

export interface OuNode { dn: string; name: string; hasChildren: boolean; allowed: boolean; reason: string | null }

export type UserPage = Paged<AdUser>;
export type ComputerPage = Paged<AdComputer>;
export type GroupPage = Paged<AdGroup>;
export type MemberPage = Paged<GroupMember>;

export const routeFor = (kind: Kind, id: string) =>
  kind === 'User' ? `/ad/users/${id}` : kind === 'Computer' ? `/ad/computers/${id}` : `/ad/groups/${id}`;
