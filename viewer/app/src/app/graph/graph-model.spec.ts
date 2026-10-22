import { isOpenGuard } from './graph-model';

describe('isOpenGuard', () => {
  it('marks only the guards the indexer emits for an opened endpoint', () => {
    expect(isOpenGuard('AllowAnonymous')).toBe(true);
    expect(isOpenGuard('DisableAntiforgery')).toBe(true);
    expect(isOpenGuard('DisableRateLimiting')).toBe(true);
    expect(isOpenGuard('RequireAuthorization')).toBe(false);
    expect(isOpenGuard('DisableSomethingElse')).toBe(false);
    expect(isOpenGuard('UseHttpsRedirection')).toBe(false);
  });
});
