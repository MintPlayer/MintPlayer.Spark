export interface ValidationError {
  attributeName: string;
  errorMessage: string;
  ruleType: string;
}

export interface ValidationErrorResponse {
  errors: ValidationError[];
}
