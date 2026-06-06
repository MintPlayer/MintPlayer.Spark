import { Provider } from '@angular/core';
import { SPARK_CONFIG, SparkConfig, defaultSparkConfig } from './spark-config';

export function provideSpark(config?: Partial<SparkConfig>): Provider[] {
  return [
    {
      provide: SPARK_CONFIG,
      useValue: { ...defaultSparkConfig, ...config }
    }
  ];
}
