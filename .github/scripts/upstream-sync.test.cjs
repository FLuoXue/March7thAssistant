'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const {execFileSync} = require('node:child_process');
const sync = require('./upstream-sync.cjs');

function temporary(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'm7a-sync-test-'));
  t.after(() => {
    // Only remove the exact directory allocated by this test, never a repository.
    assert.equal(path.dirname(dir), path.resolve(os.tmpdir()));
    assert.ok(path.basename(dir).startsWith('m7a-sync-test-'));
    fs.rmSync(dir, {recursive: true, force: true});
  });
  return dir;
}
function env(t, values) {
  const before = Object.fromEntries(Object.keys(values).map(k => [k, process.env[k]]));
  Object.assign(process.env, values);
  t.after(() => {
    for (const [k, v] of Object.entries(before)) {
      if (v === undefined) delete process.env[k]; else process.env[k] = v;
    }
  });
}
function git(dir, ...args) {
  return execFileSync('git', args, {cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim();
}
function write(dir, file, value) {
  fs.mkdirSync(path.dirname(path.join(dir, file)), {recursive: true});
  fs.writeFileSync(path.join(dir, file), value);
}
function commit(dir, message) {
  git(dir, 'add', '.'); git(dir, 'commit', '-m', message);
  return git(dir, 'rev-parse', 'HEAD');
}
function coreMock() {
  const outputs = {};
  return {outputs, info() {}, setOutput(k, v) {outputs[k] = v;},
    summary: {addRaw() {return this;}, async write() {}}};
}
function fixture(t, conflict = false) {
  const root = temporary(t), dir = path.join(root, 'repo'), stateDir = path.join(root, 'state');
  fs.mkdirSync(dir); fs.mkdirSync(stateDir);
  git(dir, 'init', '-b', 'main');
  git(dir, 'config', 'user.name', 'Sync Test'); git(dir, 'config', 'user.email', 'sync@example.invalid');
  git(dir, 'config', 'core.autocrlf', 'false'); git(dir, 'config', 'commit.gpgsign', 'false');
  write(dir, 'core.txt', 'original behavior\n');
  write(dir, 'assets/config/version.txt', 'v2026.9.30\n');
  write(dir, 'assets/docs/Changelog.md', '# 更新日志\n\n## v2026.9.30\n- original\n');
  const base = commit(dir, 'upstream base');
  git(dir, 'checkout', '-b', 'upstream');
  write(dir, 'core.txt', 'new upstream behavior\n');
  write(dir, 'assets/config/version.txt', 'v2026.10.2\n');
  write(dir, 'assets/docs/Changelog.md', '# 更新日志\n\n## v2026.10.2\n- upstream\n\n## v2026.9.30\n- original\n');
  const upstream = commit(dir, 'new upstream');
  git(dir, 'checkout', 'main');
  write(dir, 'desktop.txt', 'keep desktop sessions\n');
  if (conflict) write(dir, 'core.txt', 'fork desktop behavior\n');
  write(dir, 'assets/config/version.txt', 'v2026.10.1\n');
  write(dir, 'assets/docs/Changelog.md', '# 更新日志\n\n## v2026.10.1\n- desktop sessions\n\n## v2026.9.30\n- original\n');
  write(dir, '.github/upstream-sync.json', JSON.stringify({repository: 'moesnow/March7thAssistant', upstream_sha: base, upstream_tag: 'v2026.9.30'}));
  const old = commit(dir, 'fork desktop feature');
  // Like Actions checkout, work detached so the controller never mutates main locally.
  git(dir, 'checkout', '--detach', old);
  const state = {old, upstream, previous: base, upstreamTag: 'v2026.10.2', tag: 'v2026.10.2.post1',
    ai: false, aiRounds: 0, publish: false, rehearsal: false};
  write(stateDir, 'candidate.json', JSON.stringify(state));
  env(t, {SYNC_SOURCE: dir, SYNC_STATE: stateDir, SYNC_MODE: 'sync'});
  return {root, dir, stateDir, state, old, upstream, base};
}

test('fork versions remain greater than upstream and existing fork versions', () => {
  assert.equal(sync.nextVersion('v2026.10.2', 'v2026.10.1', []), 'v2026.10.2.post1');
  assert.equal(sync.nextVersion('v2026.9.30', 'v2026.10.1', []), 'v2026.10.1.post1');
  assert.equal(sync.nextVersion('v2026.10.2', 'v2026.10.2.post4', ['v2026.10.2.post7']), 'v2026.10.2.post8');
  assert.throws(() => sync.nextVersion('v2026.10.2-beta', 'v2026.10.1', []));
  assert.throws(() => sync.nextVersion('refs/heads/main', 'v2026.10.1', []));
});

test('changelog combines independent sections but refuses conflicting edits', () => {
  const base = '# Log\n\n## old\noriginal\n';
  const ours = '# Log\n\n## upstream\nnew upstream\n\n## old\noriginal\n';
  const theirs = '# Log\n\n## fork\ndesktop\n\n## old\noriginal\n';
  const merged = sync.mergeChangelog(base, ours, theirs);
  assert.match(merged, /## upstream/); assert.match(merged, /## fork/);
  assert.equal((merged.match(/## old/g) || []).length, 1);
  assert.throws(() => sync.mergeChangelog(base, base.replace('original', 'left'), base.replace('original', 'right')));
  assert.throws(() => sync.mergeChangelog(base, base + '\n## old\nduplicate', theirs));
});

test('real Git rebase preserves upstream, desktop features and both changelogs without AI', t => {
  const f = fixture(t), core = coreMock();
  sync.rebase({core});
  assert.equal(core.outputs.conflict, 'false');
  assert.equal(fs.readFileSync(path.join(f.dir, 'core.txt'), 'utf8'), 'new upstream behavior\n');
  assert.equal(fs.readFileSync(path.join(f.dir, 'desktop.txt'), 'utf8'), 'keep desktop sessions\n');
  sync.finalize({core});
  const state = JSON.parse(fs.readFileSync(path.join(f.stateDir, 'candidate.json')));
  assert.equal(state.ai, false);
  assert.equal(core.outputs.candidate, git(f.dir, 'rev-parse', 'HEAD'));
  assert.equal(git(f.dir, 'merge-base', f.upstream, 'HEAD'), f.upstream);
  assert.equal(git(f.dir, 'rev-parse', 'main'), f.old);
  assert.equal(fs.readFileSync(path.join(f.dir, 'assets/config/version.txt'), 'utf8'), 'v2026.10.2.post1\n');
  const log = fs.readFileSync(path.join(f.dir, 'assets/docs/Changelog.md'), 'utf8');
  assert.match(log, /## v2026.10.2.post1/); assert.match(log, /desktop sessions/); assert.match(log, /- upstream/);
  assert.ok(log.endsWith('\n') && !log.endsWith('\n\n'));
  assert.equal(git(f.dir, 'diff', '--check', f.old, core.outputs.candidate), '');
  assert.equal(git(f.dir, 'status', '--porcelain'), '');
});

test('a clean upstream update can finalize an existing changelog without adding EOF whitespace', t => {
  const f = fixture(t), core = coreMock();
  git(f.dir, 'checkout', '--detach', f.base);
  write(f.dir, 'new-upstream.txt', 'no metadata changes\n');
  const upstream = commit(f.dir, 'clean upstream update');
  git(f.dir, 'checkout', '--detach', f.old);
  write(f.stateDir, 'candidate.json', JSON.stringify({...f.state, upstream}));
  sync.rebase({core});
  assert.equal(core.outputs.conflict, 'false');
  sync.finalize({core});
  assert.equal(git(f.dir, 'diff', '--check', f.old, core.outputs.candidate), '');
});

test('a second release rebases the previously rebased history without losing fork changes', t => {
  const f = fixture(t), core = coreMock();
  sync.rebase({core}); sync.finalize({core});
  const previous = core.outputs.candidate;
  git(f.dir, 'checkout', '--detach', f.upstream);
  write(f.dir, 'second-upstream.txt', 'second release\n');
  write(f.dir, 'assets/config/version.txt', 'v2026.10.3\n');
  const next = commit(f.dir, 'next upstream release');
  git(f.dir, 'checkout', '--detach', previous);
  write(f.stateDir, 'candidate.json', JSON.stringify({...f.state, old: previous, upstream: next,
    previous: f.upstream, upstreamTag: 'v2026.10.3', tag: 'v2026.10.3.post1'}));
  sync.rebase({core}); sync.finalize({core});
  assert.equal(core.outputs.conflict, 'false');
  assert.equal(git(f.dir, 'merge-base', next, 'HEAD'), next);
  assert.match(fs.readFileSync(path.join(f.dir, 'desktop.txt'), 'utf8'), /keep desktop/);
  assert.match(fs.readFileSync(path.join(f.dir, 'second-upstream.txt'), 'utf8'), /second release/);
});

test('code conflicts stop before promotion and remain available to the AI resolver', t => {
  const f = fixture(t, true), core = coreMock();
  sync.rebase({core});
  assert.equal(core.outputs.conflict, 'true');
  assert.deepEqual(sync.conflicts(f.dir), ['core.txt']);
  assert.throws(() => sync.finalize({core}), /incomplete/);
  assert.equal(git(f.dir, 'rev-parse', 'main'), f.old);
});

test('protected files and path traversal are excluded from AI output', t => {
  const dir = temporary(t);
  for (const file of ['.github/workflows/publish.yml', '.github/upstream-sync.json', 'app/mirrorchyan.py', 'opencode.json', 'docs/AGENTS.md']) {
    assert.equal(sync.protectedFile(file), true);
  }
  for (const file of ['../outside', '/absolute', 'a/../../outside', 'a\\b']) assert.throws(() => sync.safePath(dir, file));
  assert.equal(sync.safePath(dir, 'app/file.py'), path.join(dir, 'app/file.py'));
});

test('AI approval fails closed when Environment is absent or has no required reviewers', async () => {
  const context = {repo: {owner: 'test', repo: 'repo'}};
  const mock = data => ({rest: {repos: {async getEnvironment() {return {data};}}}});
  await assert.rejects(sync.requireReviewEnvironment(mock({}), context), /Required reviewers/);
  await assert.rejects(sync.requireReviewEnvironment(mock({protection_rules: [{type: 'required_reviewers', reviewers: []}]}), context));
  await sync.requireReviewEnvironment(mock({protection_rules: [{type: 'required_reviewers', reviewers: [{id: 1}]}]}), context);
});

test('App permission inspection verifies a signed JWT and rejects pending installation grants', async t => {
  const {privateKey, publicKey} = crypto.generateKeyPairSync('rsa', {modulusLength: 2048});
  env(t, {APP_CLIENT_ID: 'test-client', APP_PRIVATE_KEY: privateKey.export({type: 'pkcs8', format: 'pem'})});
  const originalFetch = global.fetch;
  t.after(() => {global.fetch = originalFetch;});
  let installed = 'none';
  global.fetch = async (url, options) => {
    const token = options.headers.authorization.slice('Bearer '.length);
    const parts = token.split('.');
    assert.equal(parts.length, 3);
    assert.ok(crypto.verify('RSA-SHA256', Buffer.from(parts.slice(0, 2).join('.')), publicKey, Buffer.from(parts[2], 'base64url')));
    assert.equal(JSON.parse(Buffer.from(parts[1], 'base64url')).iss, 'test-client');
    return {ok: true, async json() {return {slug: 'test-app', id: 123,
      permissions: {contents: 'write', workflows: url.endsWith('/app') ? 'write' : installed}};}};
  };
  const core = {...coreMock(), setSecret() {}};
  const context = {repo: {owner: 'test', repo: 'repo'}};
  await assert.rejects(sync.checkAppPermissions({context, core}), /installed grant/);
  installed = 'write';
  await sync.checkAppPermissions({context, core});
});

function promotionFixture(t) {
  const f = fixture(t), core = coreMock();
  sync.rebase({core}); sync.finalize({core});
  const candidate = core.outputs.candidate;
  const remote = path.join(f.root, 'remote.git'); fs.mkdirSync(remote); git(remote, 'init', '--bare');
  git(f.dir, 'remote', 'add', 'origin', remote);
  git(f.dir, 'push', 'origin', `${f.old}:refs/heads/main`, `${candidate}:refs/heads/candidate`);
  const created = [];
  const github = {rest: {git: {
    async getRef({ref}) {
      const result = git(remote, 'show-ref', '--verify', '--hash', `refs/${ref}`);
      return {data: {object: {sha: result, type: 'commit'}}};
    },
    async createRef({ref, sha}) {git(remote, 'update-ref', ref, sha); created.push(ref);},
  }}};
  const originalGet = github.rest.git.getRef;
  github.rest.git.getRef = async args => {
    try {return await originalGet(args);} catch {throw Object.assign(new Error('Not found'), {status: 404});}
  };
  env(t, {CANDIDATE_SHA: candidate, OLD_SHA: f.old, RELEASE_TAG: 'v2026.10.2.post1',
    PUBLISH_ALLOWED: 'true', SYNC_MODE: 'sync', AI_USED: 'false', SYNC_PUSH_TOKEN: 'test-not-a-real-token'});
  return {...f, remote, candidate, created, github, core, context: {repo: {owner: 'test', repo: 'repo'}}};
}

test('promotion creates a backup, uses the candidate SHA and tolerates retry without moving tags', async t => {
  const f = promotionFixture(t);
  await sync.promote(f);
  assert.equal(git(f.remote, 'rev-parse', 'refs/heads/main'), f.candidate);
  assert.equal(git(f.remote, 'rev-parse', `refs/heads/backup/upstream-${f.old}`), f.old);
  assert.equal(git(f.remote, 'rev-parse', 'refs/tags/v2026.10.2.post1'), f.candidate);
  await sync.promote(f);
  assert.equal(f.created.length, 1);
});

test('a concurrent main update after the API check is rejected by the server-side Git lease', async t => {
  const f = promotionFixture(t);
  git(f.dir, 'checkout', '--detach', f.old);
  write(f.dir, 'human.txt', 'new human work\n');
  const human = commit(f.dir, 'human update');
  git(f.dir, 'push', 'origin', `${human}:refs/heads/main`);
  const get = f.github.rest.git.getRef;
  f.github.rest.git.getRef = args => args.ref === 'heads/main' ? {data: {object: {sha: f.old}}} : get(args);
  await assert.rejects(sync.promote(f), /failed/);
  assert.equal(git(f.remote, 'rev-parse', 'refs/heads/main'), human);
  assert.equal(f.created.length, 0);
});

test('an existing release tag collision stops before main is changed', async t => {
  const f = promotionFixture(t);
  git(f.remote, 'update-ref', 'refs/tags/v2026.10.2.post1', f.old);
  await assert.rejects(sync.promote(f), /Never move/);
  assert.equal(git(f.remote, 'rev-parse', 'refs/heads/main'), f.old);
});

test('rehearsal and unpublished candidates cannot be promoted', async t => {
  const f = promotionFixture(t);
  process.env.SYNC_MODE = 'rehearse';
  await assert.rejects(sync.promote(f), /not enabled/);
  assert.equal(git(f.remote, 'rev-parse', 'refs/heads/main'), f.old);
});

test('publication rejects a package for a different SHA, version or checksum', t => {
  const dir = temporary(t), sha = '1'.repeat(40), tag = 'v2026.10.2.post1', files = {};
  for (const name of [...sync.ASSETS, 'changelog_temp.md']) {
    write(dir, name, `test fixture ${name}`);
    files[name] = crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, name))).digest('hex');
  }
  write(dir, 'manifest.json', JSON.stringify({sha, tag, files}));
  sync.verifyArtifacts(dir, sha, tag);
  assert.throws(() => sync.verifyArtifacts(dir, '2'.repeat(40), tag), /match/);
  assert.throws(() => sync.verifyArtifacts(dir, sha, 'v2026.10.3.post1'), /match/);
  write(dir, 'update.7z', 'corrupted');
  assert.throws(() => sync.verifyArtifacts(dir, sha, tag), /checksum/);
});

test('a partial asset upload leaves a draft; retry completes it; published assets remain immutable', async t => {
  const dir = temporary(t), sha = '1'.repeat(40), tag = 'v2026.10.2.post1', files = {};
  for (const name of [...sync.ASSETS, 'changelog_temp.md']) {
    write(dir, name, `test fixture ${name}`);
    files[name] = crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, name))).digest('hex');
  }
  write(dir, 'manifest.json', JSON.stringify({sha, tag, files}));
  env(t, {PACKAGE_DIR: dir, CANDIDATE_SHA: sha, RELEASE_TAG: tag});
  let release, failUpload = true, publishes = 0, uploads = 0;
  const assets = [];
  const repos = {
    async listReleases() {return release ? [release] : [];},
    async listReleaseAssets() {return [...assets];},
    async createRelease(options) {release = {...options, id: 1}; return {data: release};},
    async deleteReleaseAsset({asset_id}) {assets.splice(assets.findIndex(a => a.id === asset_id), 1);},
    async uploadReleaseAsset({name}) {
      if (failUpload && name === sync.ASSETS[1]) throw new Error('simulated upload interruption');
      assets.push({name, id: ++uploads});
    },
    async updateRelease({draft}) {assert.equal(assets.length, 3); release.draft = draft; publishes++;},
  };
  const args = {github: {rest: {repos}, paginate: (method, options) => method(options)},
    context: {repo: {owner: 'test', repo: 'repo'}}, core: coreMock()};
  await assert.rejects(sync.publish(args), /interruption/);
  assert.equal(release.draft, true); assert.equal(publishes, 0);
  failUpload = false;
  await sync.publish(args);
  assert.equal(release.draft, false); assert.equal(publishes, 1);
  const completedUploads = uploads;
  await sync.publish(args);
  assert.equal(uploads, completedUploads); assert.equal(publishes, 1);
});
