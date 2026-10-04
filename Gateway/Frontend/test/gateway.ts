/*
 * A gateway that is a stand-in for fetch, and a document of happy-dom to draw
 * a page into: what the pages' tests in src/pages/*.test.ts share.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/gateway.ts';
 *   const { configurationPage } = await import('./configuration.ts');
 */

import '@node/../test/dom.ts';

import { strict as assert } from 'node:assert';

import type { Page } from '@node/router';

const { auth }            = await import('../src/auth.ts');
const { configureShell }  = await import('@node/shell.ts');


/** What the stand-in was asked: GET /configuration, with what came along. */
export interface Asked {
    method:  string;
    path:    string;
    body:    unknown;
}

/**
 * How the stand-in answers: with a value, sent as JSON with 200; with a
 * Response as it is; with undefined, as nothing it knows - 404.
 */
export type Answers = (asked: Asked) => unknown;

/** Everything asked since the page was opened, in order. */
export const asked: Asked[] = [];

let answers: Answers = () => undefined;

globalThis.fetch = (async (input: string | URL | Request, init?: RequestInit) => {

    const path   = new URL(String(input), 'http://127.0.0.1/').pathname.replace(/^\/api\/v1/, '');
    const method = init?.method ?? 'GET';
    const body   = init?.body === undefined ? undefined : JSON.parse(String(init.body)) as unknown;
    const one    = { method, path, body };

    asked.push(one);

    const answer = answers(one);

    if (answer instanceof Response)
        return answer;

    if (answer === undefined)
        return refused(404, `nothing at ${method} ${path}`);

    return new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } });

}) as typeof fetch;

/** An answer that refuses, with what the gateway says why. */
export function refused(status: number, error: string): Response {
    return new Response(JSON.stringify({ error }), { status, headers: { 'Content-Type': 'application/json' } });
}


const wait = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

/** Wait until it holds, a second at the most, and fail with what was said if it does not. */
export async function until(what: () => boolean, failure: string): Promise<void> {
    for (let i = 0; i < 200 && !what(); i++)
        await wait(5);
    assert.ok(what(), failure);
}

/**
 * A page drawn into a document of its own, for somebody with these
 * permissions, against a stand-in that answers so.
 */
export async function open(page:         Page,
                           path:         string,
                           permissions:  string[],
                           how:          Answers,
                           drawn:        (root: HTMLElement) => boolean): Promise<HTMLElement> {

    asked.length = 0;
    answers      = how;

    configureShell({ name: 'Gateway', icon: 'fa-network-wired', menu: [] });
    auth.set({ username: 'alice', roles: [ 'admin' ], permissions, mayReadTheLog: false } as never);

    const root = document.createElement('div');
    document.body.replaceChildren(root);

    page.render({ root, url: new URL(`http://127.0.0.1${path}`), params: {}, navigate: () => undefined } as never);

    await until(() => drawn(root), `${path} did not draw`);

    return root;

}
