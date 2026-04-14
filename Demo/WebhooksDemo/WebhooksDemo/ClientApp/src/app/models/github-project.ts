export interface GitHubProjectInfo {
  id: string;
  title: string;
  number: number;
  ownerLogin: string;
  ownerType: 'User' | 'Organization';
  installationId: number;
}

export interface ProjectColumn {
  optionId: string;
  name: string;
}
