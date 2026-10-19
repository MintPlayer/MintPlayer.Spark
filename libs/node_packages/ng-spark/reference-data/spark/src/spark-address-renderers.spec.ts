import { describe, expect, it } from 'vitest';
import { asSparkAddress, sparkAddressRenderers, SPARK_ADDRESS_RENDERER } from './spark-address-renderers';

describe('sparkAddressRenderers', () => {
  it('registers one renderer with all three halves under the model name', () => {
    expect(sparkAddressRenderers.length).toBe(1);
    const [registration] = sparkAddressRenderers;
    expect(registration.name).toBe(SPARK_ADDRESS_RENDERER);
    expect(registration.editComponent).toBeTruthy();
    expect(registration.detailComponent).toBeTruthy();
    expect(registration.columnComponent).toBeTruthy();
  });

  it('reads the flat form record and the nested PersistentObject alike', () => {
    const flat = { StreetName: 'Kerkstraat', PostalCode: '9770' };
    expect(asSparkAddress(flat)).toEqual(flat);
    const po = { attributes: [{ name: 'StreetName', value: 'Kerkstraat' }, { name: 'PostalCode', value: '9770' }] };
    expect(asSparkAddress(po)).toEqual(flat);
    expect(asSparkAddress(null)).toBeNull();
  });

  it('labels a filter value with its lines', () => {
    const label = sparkAddressRenderers[0].filterLabel!({ Lines: ['Kerkstraat 12', '9770 Kruishoutem'] });
    expect(label).toBe('Kerkstraat 12, 9770 Kruishoutem');
  });
});
