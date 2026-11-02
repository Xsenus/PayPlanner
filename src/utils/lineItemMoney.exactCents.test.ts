import { describe,it,expect } from 'vitest';
import { exactMoneyCents } from './lineItemMoney';
describe('exact JSON money cents',()=>{
 it.each([[0,0n],[0.29,29n],[-0.29,-29n],[12.3,1230n],[90071992547409.92,9007199254740992n],[1e21,100000000000000000000000n],[1e-2,1n]] as const)('keeps serialized decimal %s exact', (amount,expected)=>{expect(exactMoneyCents(amount)).toBe(expected);});
 it.each([0.001,1e-7,NaN,Infinity,-Infinity])('rejects fractional or nonfinite %s',value=>{expect(exactMoneyCents(value)).toBeNull();});
});
