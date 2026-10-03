import { landing, refusal } from './doors';

const owner = { id: 'v1', role: 'Owner' as const };
const staff = { id: 'v2', role: 'Staff' as const };

describe('the two doors into the venue side', () => {
  it('lets an owner in at the admin door, and staff not', () => {
    expect(refusal('admin', [owner], false)).toBeNull();
    expect(refusal('admin', [staff], false)).toBe('login.door.notAnOwner');
    // Somebody with no venue at all came in on the platform's invitation (owner-complete 3a):
    // the owner's door takes them to applying for one.
    expect(refusal('admin', [], false)).toBeNull();
    expect(landing('admin', [], false)).toBe('/venues/apply');
  });

  it("lets the platform's own people in at the admin door without a venue", () => {
    expect(refusal('admin', [], true)).toBeNull();
    expect(landing('admin', [], true)).toBe('/admin/venues');
  });

  it('lets anybody who works at a venue in at the staff door, owners included', () => {
    expect(refusal('staff', [staff], false)).toBeNull();
    expect(refusal('staff', [owner], false)).toBeNull();
    expect(refusal('staff', [], false)).toBe('login.door.notStaff');
  });

  it('takes staff with one venue to its timeline, and anybody else to every venue', () => {
    expect(landing('staff', [staff], false)).toBe('/venues/v2/timeline');
    expect(landing('staff', [owner, staff], false)).toBe('/venues');
    expect(landing('admin', [owner], false)).toBe('/venues');
  });
});
