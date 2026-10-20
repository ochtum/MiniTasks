create table public.tasks (
    id bigint generated always as identity primary key,
    owner_id uuid not null default auth.uid()
        references auth.users (id) on delete cascade,
    title text not null,
    is_completed boolean not null default false,
    created_at timestamptz not null default now(),
    constraint tasks_title_length check (
        char_length(title) between 1 and 200 and char_length(btrim(title)) > 0
    )
);

-- Supports ownership filtering, the foreign key, and newest-first task lists.
create index tasks_owner_id_id_idx on public.tasks (owner_id, id desc);

revoke all on table public.tasks from public, anon, authenticated;
grant usage on schema public to authenticated;
grant select, insert, update, delete on table public.tasks to authenticated;
grant usage on sequence public.tasks_id_seq to authenticated;

alter table public.tasks enable row level security;

create policy tasks_select_own on public.tasks
    for select to authenticated
    using ((select auth.uid()) = owner_id);

create policy tasks_insert_own on public.tasks
    for insert to authenticated
    with check ((select auth.uid()) = owner_id);

create policy tasks_update_own on public.tasks
    for update to authenticated
    using ((select auth.uid()) = owner_id)
    with check ((select auth.uid()) = owner_id);

create policy tasks_delete_own on public.tasks
    for delete to authenticated
    using ((select auth.uid()) = owner_id);
