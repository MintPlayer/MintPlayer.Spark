import { InjectionToken } from '@angular/core';

export interface SparkConfig {
  baseUrl: string;
}

export const SPARK_CONFIG = new InjectionToken<SparkConfig>('SPARK_CONFIG');

export const defaultSparkConfig: SparkConfig = {
  baseUrl: '/spark'
};
