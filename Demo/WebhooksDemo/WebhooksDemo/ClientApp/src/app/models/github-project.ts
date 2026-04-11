export interface GitHubProjectInfo {
  id: string;
  title: string;
  number: number;
  ownerLogin: string;
  ownerType: 'User' | 'Organization';
}

export interface ProjectColumn {
  optionId: string;
  name: string;
}
