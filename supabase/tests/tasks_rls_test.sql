begin;
create extension if not exists pgtap with schema extensions;
select plan(22);

select has_table('public', 'tasks', 'tasks table exists');
select col_type_is('public', 'tasks', 'id', 'bigint', 'identity uses bigint');
select ok((select relrowsecurity from pg_class where oid = 'public.tasks'::regclass), 'RLS is enabled');
select ok(not has_table_privilege('anon', 'public.tasks', 'SELECT'), 'anon has no table read grant');
select ok(not has_table_privilege('anon', 'public.tasks', 'INSERT'), 'anon has no table insert grant');

-- These transaction-only users exercise SQL policies; setup-local creates login users via Auth.
insert into auth.users (id, email) values
    ('11111111-1111-4111-8111-111111111111', 'pgtap-alice@minitasks.local'),
    ('22222222-2222-4222-8222-222222222222', 'pgtap-bob@minitasks.local');
insert into public.tasks (owner_id, title) values
    ('11111111-1111-4111-8111-111111111111', 'pgtap alice'),
    ('22222222-2222-4222-8222-222222222222', 'pgtap bob');

set local role authenticated;
select set_config('request.jwt.claims', '{"sub":"11111111-1111-4111-8111-111111111111","role":"authenticated"}', true);
select is((select count(*) from public.tasks), 1::bigint, 'Alice sees only her task');
select is((select title from public.tasks), 'pgtap alice', 'Bob task is not visible');
select lives_ok($$insert into public.tasks (title) values ('pgtap new')$$, 'Alice can insert with default owner');
select is((select owner_id::text from public.tasks where title = 'pgtap new'), '11111111-1111-4111-8111-111111111111', 'default owner comes from JWT');
select lives_ok($$update public.tasks set title = 'pgtap changed', is_completed = true where title = 'pgtap new'$$, 'Alice can update her task');
select ok((select is_completed from public.tasks where title = 'pgtap changed'), 'own completion update persisted');
select results_eq($$with changed as (update public.tasks set title = 'stolen' where owner_id = '22222222-2222-4222-8222-222222222222' returning id) select count(*) from changed$$, $$values (0::bigint)$$, 'Alice cannot update Bob task');
select results_eq($$with removed as (delete from public.tasks where owner_id = '22222222-2222-4222-8222-222222222222' returning id) select count(*) from removed$$, $$values (0::bigint)$$, 'Alice cannot delete Bob task');
select throws_ok($$insert into public.tasks (owner_id, title) values ('22222222-2222-4222-8222-222222222222', 'forged')$$, '42501', null, 'Alice cannot insert for Bob');
select throws_ok($$update public.tasks set owner_id = '22222222-2222-4222-8222-222222222222' where title = 'pgtap changed'$$, '42501', null, 'Alice cannot transfer ownership');
select throws_ok($$insert into public.tasks (title) values ('   ')$$, '23514', null, 'blank title is rejected');
select throws_ok($$insert into public.tasks (title) values (repeat('a', 201))$$, '23514', null, '201-character title is rejected');
select lives_ok($$insert into public.tasks (title) values (repeat('a', 200))$$, '200-character title is accepted');
select lives_ok($$delete from public.tasks where title = 'pgtap changed'$$, 'Alice can delete her task');
select is((select count(*) from public.tasks where title = 'pgtap changed'), 0::bigint, 'own deletion persisted');

select set_config('request.jwt.claims', '{"sub":"22222222-2222-4222-8222-222222222222","role":"authenticated"}', true);
select is((select title from public.tasks), 'pgtap bob', 'Bob still sees his unchanged task only');
reset role;
set local role anon;
select throws_ok($$select * from public.tasks$$, '42501', null, 'anonymous SQL read is denied');

select * from finish();
rollback;
