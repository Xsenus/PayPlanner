import subprocess,json,sys,datetime,hashlib,pathlib,os,re
from collections import Counter
EXPECTED_MIGRATION=None
PAYMENT_ATTEMPT_MIGRATION='20261027005432_AddPaymentCreateAttempts'
PAYMENT_ATTEMPT_DELTA_SHA256='e9a204366b5fd6df332dec56e96ca98c2f6b31778197c36c7547c9223f6e4da7'
CHAT_HISTORY_MIGRATION='20261027014154_AddPersonalChatHistory'
PAYMENT_AND_HISTORY_MIGRATIONS=PAYMENT_ATTEMPT_MIGRATION+'+'+CHAT_HISTORY_MIGRATION
PAYMENT_AND_HISTORY_DELTA_SHA256='1d53d5c0f92ce1c36fa84d49d0f257328ec7b30d8310db3d5af7b8ff310c2327'
CANDIDATE_REVIEW_SHA256='0e0df6573f9b755e94718b5cfb7d870d6554543d8b0e4b0212702e1b5fde5706'
CHAT_TABLES=['InternalChatAttachments','InternalChatMembers','InternalChatMentions','InternalChatMessages','InternalChatReactions','InternalChatThreads']
POLICY_TABLES=['Acts','Contracts','Payments']
POLICY_COLUMN='SellerSnapshotPolicy'
SUPPLEMENTAL_TABLES=['CashInventories','CashOrders','SalesRequests']
CANDIDATE_NEW_TABLES=['PaymentCreateAttempts','TaskStatusNotificationOutboxes','TaskTimeCorrections','TaskTimeEntries']
CANDIDATE_LINKED_TABLES=['TaskStatusNotificationOutboxes','TaskTimeEntries']
CANDIDATE_DEFAULTS={
 'CashInventories':({'CompanyId','MoneyAccountId'},'"CompanyId" IS NOT NULL OR "MoneyAccountId" IS NOT NULL'),
 'CashOrders':({'CompanyId','MoneyAccountId'},'"CompanyId" IS NOT NULL OR "MoneyAccountId" IS NOT NULL'),
 'SalesRequests':({'ServiceInvoiceId','ServiceInvoiceAllocationAmount'},'"ServiceInvoiceId" IS NOT NULL OR "ServiceInvoiceAllocationAmount" IS NOT NULL'),
 'TaskJournalDeadlineChangeRequests':({'ReviewerDispatchAttempts','ReviewerDispatchLeaseToken','ReviewerDispatchLeaseUntil','ReviewerDispatchPending'},'"ReviewerDispatchAttempts"<>0 OR "ReviewerDispatchLeaseToken" IS NOT NULL OR "ReviewerDispatchLeaseUntil" IS NOT NULL OR "ReviewerDispatchPending"'),
 'TaskJournalItems':({'StatusTransitionToken'},'"StatusTransitionToken"<>\'\''),
}

def need(ok,message):
 if not ok:raise AssertionError(message)

def digest(path):
 with pathlib.Path(path).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

def read_json(path):
 p=pathlib.Path(path);need(p.is_file() and not p.is_symlink(),'Missing/redirected immutable proof')
 return json.loads(p.read_text(encoding='utf-8'))

def reviewed_candidate():
 path=pathlib.Path(__file__).with_name('deploy-roadmap-candidate-review.json')
 review=read_json(path)
 # Pin parsed contents; Git/Windows newline conversion must not change approval.
 need(hashlib.sha256(canonical(review).encode()).hexdigest()==CANDIDATE_REVIEW_SHA256,'Reviewed candidate admission changed')
 need(review['schemaVersion']==1 and len(review['migrationIds'])==11 and len(set(review['migrationIds']))==11,'Exact reviewed candidate migration set required')
 return review

def reviewed_migration_ids(value):
 if value is None:return []
 if value==PAYMENT_ATTEMPT_MIGRATION:return [PAYMENT_ATTEMPT_MIGRATION]
 if value==PAYMENT_AND_HISTORY_MIGRATIONS:return [PAYMENT_ATTEMPT_MIGRATION,CHAT_HISTORY_MIGRATION]
 ids=reviewed_candidate()['migrationIds']
 need(value=='+'.join(ids),'Unreviewed migration is not authorised')
 return ids

def instant(value):
 parsed=datetime.datetime.fromisoformat(value.replace('Z','+00:00'))
 return parsed.replace(tzinfo=datetime.timezone.utc) if parsed.tzinfo is None else parsed.astimezone(datetime.timezone.utc)

def canonical(value):return json.dumps(value,sort_keys=True,separators=(',',':'))

def exact_delta(before,after):
 old=Counter(canonical(x) for x in before);new=Counter(canonical(x) for x in after)
 return {'added':[json.loads(row) for row in sorted((new-old).elements())],
         'removed':[json.loads(row) for row in sorted((old-new).elements())]}

def stable_added_trigger(row):
 # PostgreSQL embeds cluster-local OIDs in generated foreign-key trigger names.
 # Keep the complete FK identity, direction, action, enabled state and body;
 # only the engine-generated numeric name is replaced, never a user trigger.
 row=dict(row)
 if row.get('internal') is True and row.get('constraintType')=='f':
  match=re.fullmatch(r'RI_ConstraintTrigger_([ac])_[1-9][0-9]*',row['name'])
  need(match is not None,'Unexpected internal foreign-key trigger identity')
  need(all(isinstance(row.get(key),str) and row[key] for key in ['constraintName','constraintTable','constraintReferencedTable','constraintDefinition']),'Missing exact internal foreign-key binding')
  prefix='CREATE CONSTRAINT TRIGGER "'+row['name']+'" '
  need(row['definition'].startswith(prefix) and row['definition'].count(row['name'])==1,'Internal foreign-key trigger definition/name differs')
  stable='__internal_fk_trigger_'+match[1]+'__'
  row['definition']='CREATE CONSTRAINT TRIGGER "'+stable+'" '+row['definition'][len(prefix):]
  row['name']=stable
 return row

def candidate_row_controls(schema_columns,schema_objects,query):
 defaults={}
 for table,(required,predicate) in CANDIDATE_DEFAULTS.items():
  present={c['name'] for c in schema_columns if c['table']==table}&required
  need(not present or present==required,'Incomplete reviewed candidate columns: '+table)
  defaults[table]=query('SELECT to_json(count(*)) FROM "'+table+'" WHERE '+predicate+';')[0] if present else None
 relations={r['name'] for r in schema_objects['relations'] if r['kind']=='r'}
 counts={table:query('SELECT to_json(count(*)) FROM "'+table+'";')[0] if table in relations else None for table in CANDIDATE_NEW_TABLES}
 return defaults,counts

def compare_candidate_rows(a,b,migrated):
 for key,tables in [('projectionColumns',a['projectionColumns']),('supplementalProjectionColumns',SUPPLEMENTAL_TABLES)]:
  need(set(a[key])==set(tables) and a[key]==b[key],'Candidate all-column projection changed')
  for table in tables:
   need(a[key][table]==[c['name'] for c in a['schemaColumns'] if c['table']==table],'Candidate projection must include every baseline column: '+table)
 need(len(a['supplementalFingerprints'])==len(SUPPLEMENTAL_TABLES) and {r['table'] for r in a['supplementalFingerprints']}==set(SUPPLEMENTAL_TABLES) and a['supplementalFingerprints']==b['supplementalFingerprints'],'Existing cash and request rows changed')
 for key,expected in [('candidateDefaultCounts',CANDIDATE_DEFAULTS),('candidateNewTableCounts',CANDIDATE_NEW_TABLES)]:
  need(set(a[key])==set(expected) and set(b[key])==set(expected),'Candidate row-control scope changed')
  need(all(value is None for value in a[key].values()),'Candidate baseline is not the installed pre-migration state')
  need(all(value==0 and type(value) is int if migrated else value is None for value in b[key].values()),'Candidate new fields or tables contain unexpected data')

def compare_payment_attempt_schema(a,b,expected=PAYMENT_ATTEMPT_DELTA_SHA256):
 need(set(a['schemaObjects'])==set(b['schemaObjects']),'Schema object categories changed')
 owners=[r['owner'] for r in a['schemaObjects']['relations'] if r['name']=='__EFMigrationsHistory']
 need(len(owners)==1 and isinstance(owners[0],str) and owners[0],'Migration history owner unavailable')
 delta={'columns':exact_delta(a['schemaColumns'],b['schemaColumns']),
        'migrations':exact_delta(a['migrationRows'],b['migrationRows'])}
 delta.update({name:exact_delta(a['schemaObjects'][name],b['schemaObjects'][name]) for name in a['schemaObjects']})
 for category,change in delta.items():
  if category=='triggers':change['added']=[stable_added_trigger(row) for row in change['added']]
  for row in change['added']:
   if 'owner' in row:
    need(row['owner']==owners[0],'Unexpected migration object owner')
    row['owner']='__migration_owner__'
  change['added'].sort(key=canonical)
 need(hashlib.sha256(canonical(delta).encode()).hexdigest()==expected,'Unexpected migration schema or history delta')

def compare_releases(a,b,plan,phase):
 need(a['releaseSeen']==b['releaseSeen'],'Release read markers changed during read-only delivery')
 candidate=len(reviewed_migration_ids(plan['expectedMigration']))==11
 if candidate:
  review=reviewed_candidate()
  need(len(plan['expectedReleaseSeeds'])==review['releaseSeedsCount'] and hashlib.sha256(json.dumps(plan['expectedReleaseSeeds'],ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest()==review['releaseSeedsSha256'],'Exact reviewed candidate release seeds required')
 if phase=='unchanged':
  need(a['releaseRows']==b['releaseRows'] and a['releaseItems']==b['releaseItems'],'Release catalog changed before startup')
  return
 old={r['ReleaseId']:r for r in a['releaseRows']};new={r['ReleaseId']:r for r in b['releaseRows']}
 need(len(old)==len(a['releaseRows']) and len(new)==len(b['releaseRows']),'Duplicate release identity')
 seeds={r['releaseId']:r for r in plan['expectedReleaseSeeds']}
 need(len(seeds)==len(plan['expectedReleaseSeeds']) and len(seeds)<=(review['releaseSeedsCount'] if candidate else 20),'Exact reviewed release seeds required')
 need(not set(old)&set(seeds) and set(new)==set(old)|set(seeds),'Unexpected release insertion/removal or stale baseline')
 for identity,row in old.items():need(new[identity]==row,'Previously published/manual release row changed')
 old_ids={r['Id'] for r in old.values()}
 need([r for r in b['releaseItems'] if r['AppReleaseId'] in old_ids]==a['releaseItems'],'Previously published/manual release items changed')
 need(len({r['Id'] for r in b['releaseRows']})==len(b['releaseRows']),'Duplicate release numeric identity')
 need(len({r['Id'] for r in b['releaseItems']})==len(b['releaseItems']),'Duplicate release item numeric identity')
 for identity,seed in seeds.items():
  row=new[identity];need(type(row['Id']) is int and row['Id']>0 and row['Id'] not in old_ids,'New release numeric identity invalid')
  expected={'Id':row['Id'],'ReleaseId':identity,'Version':seed['version'].strip(),'ReleasedAt':row['ReleasedAt'],'Title':seed['title'].strip(),'Summary':seed['summary'].strip(),'IsActive':seed['isActive'],'Source':seed.get('source','agent').strip(),'CreatedAt':row['CreatedAt'],'UpdatedAt':row['UpdatedAt'],'CreatedByUserId':None,'CreatedByUserName':None,'UpdatedByUserId':None,'UpdatedByUserName':None}
  need(row==expected and instant(row['ReleasedAt'])==instant(seed['releasedAt']),'Unexpected new release contents')
  need(instant(a['utc'])<=instant(row['CreatedAt'])<=instant(b['utc']) and row['CreatedAt']==row['UpdatedAt'],'New release timestamps outside stopped-baseline/startup window')
  actual=sorted([r for r in b['releaseItems'] if r['AppReleaseId']==row['Id']],key=lambda r:(r['SortOrder'],r['Id']))
  expected_items=[{'Id':item['Id'],'AppReleaseId':row['Id'],'Type':s['type'].strip().lower(),'Text':s['text'].strip(),'SortOrder':n} for n,(item,s) in enumerate(zip(actual,seed['items']))]
  need(len(actual)==len(seed['items']) and actual==expected_items,'New release item content/order differs')
 need(all(r['AppReleaseId'] in {x['Id'] for x in b['releaseRows']} for r in b['releaseItems']),'Orphan release item')

def compare(plan_path,a_path,b_path,phase):
 need(phase in ['unchanged','startup'],'Unknown comparison boundary')
 plan,a,b=map(read_json,[plan_path,a_path,b_path])
 admitted_ids=reviewed_migration_ids(plan['expectedMigration'])
 candidate=len(admitted_ids)==11
 need(a['schemaVersion']==b['schemaVersion']==2 and a['baselineSha256'] is None,'Immutable stopped baseline required')
 need(b['baselineSha256']==digest(a_path),'Later proof not bound to exact baseline bytes')
 need(a['fingerprints']==b['fingerprints'],'Protected rows/counts/all-column contents changed')
 need(a['projectionColumns']==b['projectionColumns'],'Pinned all-column projection changed')
 migrated=phase=='startup' and bool(admitted_ids)
 history=plan['expectedMigration']==PAYMENT_AND_HISTORY_MIGRATIONS or candidate
 if candidate:compare_candidate_rows(a,b,migrated)
 if history:
  need(set(a['chatProjectionColumns'])==set(CHAT_TABLES) and a['chatProjectionColumns']==b['chatProjectionColumns'],'Chat projection scope changed')
  for table in CHAT_TABLES:
   need(a['chatProjectionColumns'][table]==[c['name'] for c in a['schemaColumns'] if c['table']==table],'Chat baseline projection must include every existing column')
  need(len(a['chatFingerprints'])==len(CHAT_TABLES) and {row['table'] for row in a['chatFingerprints']}==set(CHAT_TABLES) and a['chatFingerprints']==b['chatFingerprints'],'Existing chat rows or read states changed')
  need(a['chatHistoryNonDefaults'] is None,'Unexpected pre-existing history columns')
  need(b['chatHistoryNonDefaults']==(0 if migrated else None),'Existing personal history state changed')
 if migrated:compare_payment_attempt_schema(a,b,reviewed_candidate()['schemaDeltaSha256'] if candidate else PAYMENT_AND_HISTORY_DELTA_SHA256 if history else PAYMENT_ATTEMPT_DELTA_SHA256)
 else:need(a['schemaColumns']==b['schemaColumns'] and a['schemaObjects']==b['schemaObjects'],'Unexpected schema mutation')
 need({k:v for k,v in a['controls'].items() if k!='migrations'}=={k:v for k,v in b['controls'].items() if k!='migrations'},'Protected financial/integrity controls changed')
 am=a['controls']['migrations']
 need(len(am)==209 and am==plan['expectedBaselineMigrations'],'Baseline is not the accepted installed142 migration catalog')
 need(hashlib.sha256(json.dumps(am,sort_keys=True,separators=(',',':')).encode()).hexdigest()==plan['expectedBaselineMigrationIdsSha256'],'Baseline migration catalog pin differs')
 need(len(a['migrationRows'])==209 and a['migrationRows']==plan['expectedBaselineMigrationRows'],'All209 full baseline migration rows/ProductVersion must remain exact')
 if migrated:need(b['controls']['migrations']==sorted(am+admitted_ids),'Unexpected migration IDs after startup')
 else:need(a['controls']==b['controls'] and a['migrationRows']==b['migrationRows'],'Migration history or ProductVersion changed before authorised startup')
 need(all(a['controls'][n]==0 for n in ['legacyActive','pendingContractor','invalidIndexes','invalidConstraints']),'Unsafe baseline control')
 need(set(a['projectionColumns'])==set(plan['expectedProtectedTables']) and len(a['fingerprints'])==16,'Exact protected16 table scope required')
 for table in POLICY_TABLES:
  columns=[c for c in a['schemaColumns'] if c['table']==table]
  need(a['projectionColumns'][table]==[c['name'] for c in columns] and POLICY_COLUMN in a['projectionColumns'][table],'Seller policy must participate in complete row fingerprints')
  policy=[c for c in columns if c['name']==POLICY_COLUMN]
  need(len(policy)==1,'Missing or duplicate installed Seller policy column')
  c=policy[0]
  need(c['type']=='character varying(32)' and c['nullable'] is True and c['default'] is None and c['identity']=='' and c['generated']=='' and c['collation'] in ['pg_catalog.default','pg_catalog."default"'],'Installed Seller policy shape changed')
 need(a['newPolicyNonNullCounts']==b['newPolicyNonNullCounts'] and set(a['newPolicyNonNullCounts'])==set(POLICY_TABLES) and all(type(v) is int and v>=0 for v in a['newPolicyNonNullCounts'].values()),'Existing policy values/counts changed')
 compare_releases(a,b,plan,'unchanged' if phase=='unchanged' or not plan['expectedReleaseSeeds'] else 'startup')
 print(json.dumps({'status':'passed','phase':phase,'protectedTables':16,'protectedChatTables':len(CHAT_TABLES) if history else 0,'protectedSupplementalTables':len(SUPPLEMENTAL_TABLES) if candidate else 0,'allColumnsAndRowsExact':True,'migrationHistory209Exact':True,'releaseCatalogUnchanged':a['releaseRows']==b['releaseRows'] and a['releaseItems']==b['releaseItems'],'reviewedReleaseSeeds':len(plan['expectedReleaseSeeds']) if phase=='startup' else 0,'baselineSha256':digest(a_path)}))

def main():
 if len(sys.argv)>1 and sys.argv[1]=='--compare':
  need(len(sys.argv)==6,'Expected plan/baseline/current/phase')
  compare(*sys.argv[2:]);sys.exit(0)

 need(len(sys.argv) in [2,3],'Expected output path and optional immutable baseline')
 baseline=read_json(sys.argv[2]) if len(sys.argv)==3 else None
 baseline_sha=digest(sys.argv[2]) if baseline is not None else None

 def sql_literal(value):return "'"+value.replace("'","''")+"'"
 def sql_ident(value):return '"'+value.replace('"','""')+'"'


 PSQL=['sudo','-n','-u','postgres','psql','-X','-q','-At','-v','ON_ERROR_STOP=1','-d','payplanner']
 def query(sql):
  r=subprocess.run(PSQL,input="BEGIN READ ONLY; SET LOCAL statement_timeout='30s'; SET LOCAL lock_timeout='2s'; SET LOCAL jit=off;\n"+sql+'\nCOMMIT;',text=True,capture_output=True,timeout=45)
  if r.returncode:raise RuntimeError(r.stderr[:1500])
  return [json.loads(x) for x in r.stdout.splitlines() if x.strip()]
 tables=query('''SELECT to_json(c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind='r' AND (c.relname IN ('TaskJournalItems','Payments','PaymentResponsibles','PaymentRemainderTransfers','MoneyOperations','Invoices') OR c.oid IN (SELECT conrelid FROM pg_constraint WHERE contype='f' AND confrelid='"TaskJournalItems"'::regclass)) AND c.relname NOT IN ('GoogleCalendarSyncLogs','YandexCalendarSyncLogs','UserNotifications','InternalChatMessages','InternalChatThreads') ORDER BY c.relname;''')
 assert {'TaskJournalItems','GoogleCalendarTaskLinks','YandexCalendarTaskLinks','Payments','MoneyOperations','Invoices'}.issubset(tables)
 # Keep the original dynamically discovered protected set; add the two affected document tables.
 # Only these two reviewed new tables can join the original dynamically found
 # task set; their empty startup state is checked separately, never ignored.
 tables=sorted((set(tables)|{'Acts','Contracts'})-set(CANDIDATE_LINKED_TABLES))
 schema_columns=query('''SELECT jsonb_build_object('table',c.relname,'name',a.attname,'position',a.attnum,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull,'default',pg_get_expr(d.adbin,d.adrelid),'identity',a.attidentity,'generated',a.attgenerated,'collation',CASE WHEN a.attcollation=0 THEN NULL ELSE quote_ident(cn.nspname)||'.'||quote_ident(co.collname) END) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum LEFT JOIN pg_collation co ON co.oid=a.attcollation LEFT JOIN pg_namespace cn ON cn.oid=co.collnamespace WHERE n.nspname='public' AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname,a.attnum;''')
 schema_objects={}
 schema_objects['relations']=query('''SELECT jsonb_build_object('name',c.relname,'kind',c.relkind,'persistence',c.relpersistence,'owner',pg_get_userbyid(c.relowner),'options',c.reloptions,'rowSecurity',c.relrowsecurity,'forceRowSecurity',c.relforcerowsecurity,'partition',CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound,c.oid) ELSE NULL END,'view',CASE WHEN c.relkind IN ('v','m') THEN pg_get_viewdef(c.oid,true) ELSE NULL END) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' ORDER BY c.relname;''')
 schema_objects['constraints']=query('''SELECT jsonb_build_object('table',r.relname,'name',c.conname,'definition',pg_get_constraintdef(c.oid,true),'validated',c.convalidated,'deferrable',c.condeferrable,'deferred',c.condeferred) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace LEFT JOIN pg_class r ON r.oid=c.conrelid WHERE n.nspname='public' ORDER BY r.relname,c.conname;''')
 schema_objects['indexes']=query('''SELECT jsonb_build_object('table',r.relname,'name',x.relname,'definition',pg_get_indexdef(i.indexrelid),'valid',i.indisvalid,'ready',i.indisready,'live',i.indislive) FROM pg_index i JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_class r ON r.oid=i.indrelid JOIN pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname='public' ORDER BY r.relname,x.relname;''')
 schema_objects['triggers']=query('''SELECT jsonb_build_object('table',r.relname,'name',t.tgname,'definition',pg_get_triggerdef(t.oid,true),'enabled',t.tgenabled,'internal',t.tgisinternal,'constraintName',c.conname,'constraintType',c.contype,'constraintTable',cr.relname,'constraintReferencedTable',rr.relname,'constraintDefinition',CASE WHEN c.oid IS NULL THEN NULL ELSE pg_get_constraintdef(c.oid,true) END) FROM pg_trigger t JOIN pg_class r ON r.oid=t.tgrelid JOIN pg_namespace n ON n.oid=r.relnamespace LEFT JOIN pg_constraint c ON c.oid=t.tgconstraint LEFT JOIN pg_class cr ON cr.oid=c.conrelid LEFT JOIN pg_class rr ON rr.oid=c.confrelid WHERE n.nspname='public' ORDER BY r.relname,t.tgname;''')
 schema_objects['routines']=query('''SELECT jsonb_build_object('name',p.proname,'identity',pg_get_function_identity_arguments(p.oid),'kind',p.prokind,'definitionMd5',md5(CASE WHEN p.prokind IN ('f','p') THEN pg_get_functiondef(p.oid) ELSE p.prosrc END),'owner',pg_get_userbyid(p.proowner),'config',p.proconfig) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' ORDER BY p.proname,pg_get_function_identity_arguments(p.oid);''')
 schema_objects['sequences']=query('''SELECT jsonb_build_object('name',c.relname,'type',format_type(s.seqtypid,NULL),'start',s.seqstart,'increment',s.seqincrement,'max',s.seqmax,'min',s.seqmin,'cache',s.seqcache,'cycle',s.seqcycle) FROM pg_sequence s JOIN pg_class c ON c.oid=s.seqrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' ORDER BY c.relname;''')
 schema_objects['policies']=query('''SELECT to_jsonb(p) FROM pg_policies p WHERE p.schemaname='public' ORDER BY p.tablename,p.policyname;''')
 schema_objects['types']=query('''SELECT jsonb_build_object('name',t.typname,'kind',t.typtype,'baseType',format_type(t.typbasetype,t.typtypmod),'notNull',t.typnotnull,'default',t.typdefault,'owner',pg_get_userbyid(t.typowner),'enumLabels',(SELECT jsonb_agg(e.enumlabel ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid=t.oid)) FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public' ORDER BY t.typname;''')
 migration_rows=query('''SELECT to_jsonb(h) FROM "__EFMigrationsHistory" h ORDER BY "MigrationId";''')
 projection_columns={t:[c['name'] for c in schema_columns if c['table']==t] for t in tables} if baseline is None else baseline['projectionColumns']
 need(set(projection_columns)==set(tables),'Pinned protected table set differs')
 chat_columns={t:[c['name'] for c in schema_columns if c['table']==t] for t in CHAT_TABLES} if baseline is None else baseline['chatProjectionColumns']
 need(set(chat_columns)==set(CHAT_TABLES) and all(chat_columns[t] for t in CHAT_TABLES),'Pinned chat table scope differs')
 supplemental_columns={t:[c['name'] for c in schema_columns if c['table']==t] for t in SUPPLEMENTAL_TABLES} if baseline is None else baseline['supplementalProjectionColumns']
 need(set(supplemental_columns)==set(SUPPLEMENTAL_TABLES) and all(supplemental_columns.values()),'Pinned cash/request table scope differs')
 candidate_defaults,candidate_new_tables=candidate_row_controls(schema_columns,schema_objects,query)
 history_columns={c['name'] for c in schema_columns if c['table']=='InternalChatMembers'}
 new_history_columns={'HistoryHiddenThroughMessageId','HistoryVisibilityRevision'}
 need(not (history_columns & new_history_columns) or new_history_columns.issubset(history_columns),'Incomplete personal history schema')
 history_non_defaults=query('SELECT to_json(count(*)) FROM "InternalChatMembers" WHERE "HistoryHiddenThroughMessageId" IS NOT NULL OR "HistoryVisibilityRevision"<>0;')[0] if new_history_columns.issubset(history_columns) else None
 policy_non_null={}
 for table in POLICY_TABLES:
  exists=any(c['table']==table and c['name']==POLICY_COLUMN for c in schema_columns)
  policy_non_null[table]=query('SELECT to_json(count(*)) FROM public.'+sql_ident(table)+' WHERE '+sql_ident(POLICY_COLUMN)+' IS NOT NULL;')[0] if exists else None
 release_rows=query('''SELECT to_jsonb(r) FROM "AppReleases" r ORDER BY "ReleaseId";''')
 release_items=query('''SELECT to_jsonb(r) FROM "AppReleaseItems" r ORDER BY "Id";''')
 release_seen=query('''SELECT jsonb_build_object('count',count(*),'hash',md5(coalesce(string_agg(md5(to_jsonb(t)::text),'' ORDER BY to_jsonb(t)->>'Id',to_jsonb(t)::text),''))) FROM "AppReleaseSeen" t;''')[0]

 def fingerprints(targets,projections):
  checks=[]
  for table in targets:
   assert table.replace('_','').isalnum()
   columns=projections[table]
   assert columns and len(columns)==len(set(columns))
   selected=','.join('t.'+sql_ident(n) for n in columns)
   checks.append(query("SELECT jsonb_build_object('table',"+sql_literal(table)+",'count',count(*),'hash',md5(coalesce(string_agg(md5(rowdata::text),'' ORDER BY rowdata->>'Id',rowdata::text),'')))::text FROM (SELECT to_jsonb(original) AS rowdata FROM (SELECT "+selected+" FROM public."+sql_ident(table)+" t) original) projected;")[0])
  return checks
 checks=fingerprints(tables,projection_columns)
 chat_checks=fingerprints(CHAT_TABLES,chat_columns)
 supplemental_checks=fingerprints(SUPPLEMENTAL_TABLES,supplemental_columns)
 controls=query('''SELECT jsonb_build_object('legacyActive',(SELECT count(*) FROM "TaskJournalItems" WHERE "ClientBillingClassification"='legacy' AND NOT "IsDeleted"),'pendingContractor',(SELECT count(*) FROM "TaskJournalItems" t JOIN "Users" u ON u."Id"=t."AssigneeUserId" WHERE NOT t."IsDeleted" AND t."ContractorPaymentEnabled" AND t."ContractorPaymentId" IS NULL AND u."IsExternalContractor" AND round(t."ContractorPaymentAmount",2)>0 AND ((NOT t."ContractorPaymentOnResult" AND t."ContractorPaymentDueDate" IS NOT NULL) OR (t."ContractorPaymentOnResult" AND lower(t."Status")='done' AND t."ContractorPaymentResultDelayDays" BETWEEN 0 AND 365))),'invalidIndexes',(SELECT count(*) FROM pg_index WHERE NOT indisvalid),'invalidConstraints',(SELECT count(*) FROM pg_constraint WHERE NOT convalidated),'migrations',(SELECT jsonb_agg("MigrationId" ORDER BY "MigrationId") FROM "__EFMigrationsHistory"));''')[0]
 assert controls['legacyActive']==controls['pendingContractor']==controls['invalidIndexes']==controls['invalidConstraints']==0
 out=dict(schemaVersion=2,utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),fingerprints=checks,controls=controls,baselineSha256=baseline_sha,projectionColumns=projection_columns,chatProjectionColumns=chat_columns,chatFingerprints=chat_checks,chatHistoryNonDefaults=history_non_defaults,supplementalProjectionColumns=supplemental_columns,supplementalFingerprints=supplemental_checks,candidateDefaultCounts=candidate_defaults,candidateNewTableCounts=candidate_new_tables,schemaColumns=schema_columns,schemaObjects=schema_objects,migrationRows=migration_rows,newPolicyNonNullCounts=policy_non_null,releaseRows=release_rows,releaseItems=release_items,releaseSeen=release_seen)
 target=pathlib.Path(sys.argv[1]);target.write_text(json.dumps(out,indent=2));os.chmod(target,0o600)
 print(json.dumps(dict(tables=len(checks),tasks=next(t['count'] for t in checks if t['table']=='TaskJournalItems'),calendarLinks=sum(t['count'] for t in checks if t['table'] in ('GoogleCalendarTaskLinks','YandexCalendarTaskLinks')),pendingTaskBackfills=0)))

if __name__ == '__main__':
 main()
